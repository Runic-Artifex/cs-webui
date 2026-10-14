using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CsWebUi.Internal;
using CsWebUi.Native;

namespace CsWebUi;

/// <summary>Owns a native WebUI window and its managed callback bindings.</summary>
public sealed unsafe class WebUiWindow : IDisposable
{
    // StartServer selects a free port itself and retries this often when listening fails (#18).
    private const int StartServerAttempts = 4;

    private static readonly ConcurrentDictionary<nuint, WebUiWindow> Windows = new();
    private static readonly byte[] FileHandlerFailureResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 500 Internal Server Error\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "Content-Length: 21\r\n" +
        "Cache-Control: no-store\r\n" +
        "X-Content-Type-Options: nosniff\r\n" +
        "\r\n" +
        "Internal Server Error");

    [ThreadStatic]
    private static int t_dispatchDepth;

    private readonly ConcurrentDictionary<nuint, BindingRegistration> _bindings = new();
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly nuint _id;
    private readonly int _generation = WebUiNativeLifetime.Generation;
    private FileHandlerRegistration? _fileHandler;
    private int _callbacksInFlight;
    private int _customPort;
    private int _serverRequested;
    private int _disposeRequested;
    private int _shutdownCancelled;
    private int _destroyed;

    /// <summary>Creates a new WebUI window with an automatically allocated identifier.</summary>
    /// <exception cref="WebUiException">The native runtime could not allocate the window.</exception>
    public WebUiWindow()
        : this(WebUiNative.NewWindow(), true)
    {
    }

    /// <summary>Creates a new WebUI window with a caller-selected identifier.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowId"/> is zero.</exception>
    /// <exception cref="WebUiException">The requested identifier is already in use or could not be allocated.</exception>
    public WebUiWindow(nuint windowId)
        : this(CreateWithId(windowId), true)
    {
    }

    private WebUiWindow(nuint id, bool _)
    {
        if (id == 0)
        {
            throw WebUiApplication.CreateNativeException();
        }

        _id = id;
        if (!Windows.TryAdd(id, this))
        {
            WebUiNative.Destroy(id);
            throw new InvalidOperationException($"A managed WebUI window with id {id} already exists.");
        }
    }

    /// <summary>Gets the native identifier for this window.</summary>
    public nuint Id => _id;

    /// <summary>Gets whether the native window is currently shown.</summary>
    public bool IsShown
    {
        get
        {
            ThrowIfDisposed();
            return WebUiNative.IsShown(_id) != 0;
        }
    }

    /// <summary>Gets the URL served for this window, if it is running.</summary>
    public string? Url
    {
        get
        {
            ThrowIfDisposed();
            return Utf8.Decode(WebUiNative.GetUrl(_id));
        }
    }

    /// <summary>Gets the native server port for this window.</summary>
    public nuint Port
    {
        get
        {
            ThrowIfDisposed();
            return WebUiNative.GetPort(_id);
        }
    }

    /// <summary>Gets the operating-system process identifier of the browser started for this window.</summary>
    public nuint BrowserProcessId
    {
        get
        {
            ThrowIfDisposed();
            return WebUiNative.GetChildProcessId(_id);
        }
    }

    /// <summary>Gets the browser WebUI considers the best available choice for this window.</summary>
    public WebUiBrowser BestBrowser
    {
        get
        {
            ThrowIfDisposed();
            return (WebUiBrowser)(uint)WebUiNative.GetBestBrowser(_id);
        }
    }

    /// <summary>Binds an element or JavaScript object to a synchronous action.</summary>
    /// <param name="element">The element name; an empty string receives every event.</param>
    /// <param name="handler">The callback to invoke.</param>
    public WebUiBinding Bind(string element, Action<WebUiEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return BindCore(element, new BindingRegistration((webUiEvent, _) =>
        {
            handler(webUiEvent);
            return ValueTask.FromResult(WebUiResult.None);
        }, isAsync: false));
    }

    /// <summary>Binds an element or JavaScript object to a synchronous result-producing callback.</summary>
    /// <param name="element">The element name; an empty string receives every event.</param>
    /// <param name="handler">The callback to invoke.</param>
    public WebUiBinding Bind(string element, Func<WebUiEvent, WebUiResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return BindCore(element, new BindingRegistration((webUiEvent, _) => ValueTask.FromResult(handler(webUiEvent)), isAsync: false));
    }

    /// <summary>Binds an element or JavaScript object to an asynchronous callback.</summary>
    /// <remarks>
    /// Register asynchronous bindings before showing a window when possible. WebUI's global
    /// asynchronous-response mode is enabled when this method is first used.
    /// </remarks>
    public WebUiBinding BindAsync(string element, Func<WebUiEvent, CancellationToken, ValueTask<WebUiResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        WebUiApplication.SetConfiguration(WebUiConfiguration.AsynchronousResponse, true);
        return BindCore(element, new BindingRegistration(handler, isAsync: true));
    }

    /// <summary>Shows HTML content, a local file, or a URL using WebUI's recommended browser.</summary>
    /// <remarks>
    /// WebUI tries its preferred installed browser, then the system's default browser, then the
    /// embedded WebView. A browser selected with <see cref="WebUiApplication.SetBrowserPath"/> or
    /// <c>CSWEBUI_BROWSER_PATH</c> is used instead, without fallback.
    /// </remarks>
    /// <exception cref="WebUiShowException">
    /// WebUI could not show the window. The exception names the browser WebUI tried, where it was
    /// found, and whether it was missing, could not be started, or did not connect in time.
    /// </exception>
    public void Show(string content)
    {
        ThrowIfDisposed();
        var configured = WebUiApplication.ApplyConfiguredBrowser();
        var requested = configured?.Browser ?? WebUiBrowser.AnyBrowser;
        var started = Stopwatch.GetTimestamp();
        var shown = configured is null ? ShowNative(content) : ShowInBrowserNative(content, requested);
        if (!shown)
        {
            throw CreateShowException(requested, Stopwatch.GetElapsedTime(started), configured?.Path);
        }
    }

    /// <summary>Attempts to show HTML content, a local file, or a URL using WebUI's recommended browser.</summary>
    /// <remarks>Honors <see cref="WebUiApplication.SetBrowserPath"/> and <c>CSWEBUI_BROWSER_PATH</c> like <see cref="Show"/>.</remarks>
    public bool TryShow(string content)
    {
        ThrowIfDisposed();
        var configured = WebUiApplication.ApplyConfiguredBrowser();
        return configured is null ? ShowNative(content) : ShowInBrowserNative(content, configured.Browser);
    }

    /// <summary>Shows HTML content, a local file, or a URL in a selected browser.</summary>
    /// <exception cref="WebUiShowException">WebUI could not show the window in that browser.</exception>
    public void ShowInBrowser(string content, WebUiBrowser browser)
    {
        ThrowIfDisposed();
        var configured = WebUiApplication.ApplyConfiguredBrowser();
        var started = Stopwatch.GetTimestamp();
        if (!ShowInBrowserNative(content, browser))
        {
            var path = configured is not null && configured.Browser == browser ? configured.Path : null;
            throw CreateShowException(browser, Stopwatch.GetElapsedTime(started), path);
        }
    }

    /// <summary>Attempts to show HTML content, a local file, or a URL in a selected browser.</summary>
    public bool TryShowInBrowser(string content, WebUiBrowser browser)
    {
        ThrowIfDisposed();
        WebUiApplication.ApplyConfiguredBrowser();
        return ShowInBrowserNative(content, browser);
    }

    /// <summary>Shows HTML content, a local file, or a URL in an embedded WebView.</summary>
    /// <exception cref="WebUiShowException">The native runtime could not show the WebView.</exception>
    public void ShowWebView(string content)
    {
        ThrowIfDisposed();
        var started = Stopwatch.GetTimestamp();
        if (!TryShowWebView(content))
        {
            throw CreateShowException(WebUiBrowser.WebView, Stopwatch.GetElapsedTime(started), null);
        }
    }

    /// <summary>Attempts to show HTML content, a local file, or a URL in an embedded WebView.</summary>
    public bool TryShowWebView(string content)
    {
        ThrowIfDisposed();
        var bytes = Utf8.Encode(content, nameof(content));
        Volatile.Write(ref _serverRequested, 1);
        fixed (byte* value = bytes)
        {
            return WebUiNative.ShowWebView(_id, value) != 0;
        }
    }

    private bool ShowNative(string content)
    {
        var bytes = Utf8.Encode(content, nameof(content));
        Volatile.Write(ref _serverRequested, 1);
        fixed (byte* value = bytes)
        {
            return WebUiNative.Show(_id, value) != 0;
        }
    }

    private bool ShowInBrowserNative(string content, WebUiBrowser browser)
    {
        var bytes = Utf8.Encode(content, nameof(content));
        Volatile.Write(ref _serverRequested, 1);
        fixed (byte* value = bytes)
        {
            return WebUiNative.ShowBrowser(_id, value, (nuint)browser) != 0;
        }
    }

    private WebUiShowException CreateShowException(WebUiBrowser requested, TimeSpan elapsed, string? configuredPath)
    {
        var tried = requested;
        if (requested == WebUiBrowser.AnyBrowser)
        {
            // The same choice WebUI made: a running browser first, then its preference order.
            tried = (WebUiBrowser)(uint)WebUiNative.GetBestBrowser(_id);
        }

        var path = configuredPath;
        if (path is null && tried is not (WebUiBrowser.AnyBrowser or WebUiBrowser.ChromiumBased or WebUiBrowser.WebView))
        {
            path = WebUiBrowserLocator.Find(tried, WebUiApplication.BrowserFolder);
        }

        return BuildShowException(_id, requested, tried, path, elapsed, WebUiApplication.ShowConnectionTimeout, HasDisplay());
    }

    internal static WebUiShowException BuildShowException(
        nuint windowId,
        WebUiBrowser requested,
        WebUiBrowser tried,
        string? browserPath,
        TimeSpan elapsed,
        TimeSpan connectionTimeout,
        bool hasDisplay)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var message = new StringBuilder(string.Create(invariant, $"WebUI could not show window {windowId}: "));
        WebUiShowFailure failure;
        if (tried == WebUiBrowser.WebView)
        {
            failure = WebUiShowFailure.WebViewNotStarted;
            message.Append("the embedded WebView could not be started. It needs ")
                .Append(OperatingSystem.IsWindows() ? "the Microsoft Edge WebView2 Runtime"
                    : OperatingSystem.IsMacOS() ? "WKWebView"
                    : "GTK 3, WebKitGTK 4.1 and a display")
                .Append('.');
        }
        else if (tried is WebUiBrowser.AnyBrowser)
        {
            failure = WebUiShowFailure.BrowserNotFound;
            message.Append("it found no supported browser (Chrome, Edge, Chromium, Epic, Vivaldi, Brave, Firefox or Yandex)");
            if (requested == WebUiBrowser.AnyBrowser)
            {
                message.Append(", and the embedded WebView could not be started instead");
            }

            message.Append('.');
        }
        else
        {
            var name = WebUiBrowserLocator.DisplayName(tried);
            var where = browserPath is null ? string.Empty : $" ({browserPath})";
            if (elapsed >= connectionTimeout - TimeSpan.FromSeconds(1))
            {
                failure = WebUiShowFailure.NotConnected;
                message.Append(invariant, $"it started {name}{where}, but the page did not connect within {connectionTimeout.TotalSeconds:0} s.");
            }
            else
            {
                failure = WebUiShowFailure.BrowserNotStarted;
                message.Append(invariant, $"it could not start {name}{where}");
                message.Append(requested == WebUiBrowser.AnyBrowser
                    ? ", the system's default browser, or the embedded WebView."
                    : ".");
            }

            message.Append(" WebUI starts browsers detached and discards their output, so the browser's exit code and errors are not available; run it with the window's URL to see why it fails.");
        }

        if (!hasDisplay)
        {
            message.Append(" No display is available: DISPLAY and WAYLAND_DISPLAY are not set.");
        }

        if (tried != WebUiBrowser.WebView)
        {
            message.Append(invariant, $" Choose the browser with {WebUiApplication.BrowserPathEnvironmentVariable}, WebUiApplication.SetBrowserPath or WebUiWindow.ShowInBrowser.");
        }

        return new WebUiShowException(
            message.ToString(),
            failure,
            tried,
            browserPath,
            failure == WebUiShowFailure.NotConnected ? connectionTimeout : null);
    }

    private static bool HasDisplay()
        => !OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>Starts the local WebUI server without launching a browser.</summary>
    /// <returns>The absolute URL of the started server.</returns>
    /// <remarks>
    /// Unless <see cref="SetPort"/> chose a port, the first start takes a free port from the
    /// operating system and retries a few times with a new one when listening fails. WebUI's own
    /// choice repeats across processes started in the same second, so concurrent hosts collided.
    /// WebUI also resets <see cref="WebUiApplication.SetConnectionTimeout"/> to zero here.
    /// </remarks>
    /// <exception cref="WebUiException">
    /// The native runtime could not start the server. WebUI reports this by returning an empty URL,
    /// for example when the window's server did not start listening in time. The failed call can
    /// be retried; WebUI then selects a new port.
    /// </exception>
    public string StartServer(string content)
    {
        ThrowIfDisposed();
        var bytes = Utf8.Encode(content, nameof(content));
        for (var attempt = 1; ; attempt++)
        {
            var selectedPort = Volatile.Read(ref _customPort) == 0
                && Volatile.Read(ref _serverRequested) == 0
                && TrySelectFreePort();
            string? result;
            fixed (byte* value = bytes)
            {
                result = Utf8.Decode(WebUiNative.StartServer(_id, value));
            }

            // webui_start_server() makes WebUI wait forever for connections.
            WebUiApplication.ResetConnectionTimeoutMirror();
            if (IsServerUrl(result))
            {
                Volatile.Write(ref _serverRequested, 1);
                return result!;
            }

            if (!selectedPort || attempt == StartServerAttempts)
            {
                return RequireServerUrl(result, _id, WebUiApplication.ReadNativeError);
            }
        }
    }

    private bool TrySelectFreePort()
    {
        // WebUI draws ports from rand() seeded with the current second, so processes started in
        // the same second try the same ports and all but one fail to listen (#18). The operating
        // system hands out a distinct free port instead.
        int port;
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        }
        catch (SocketException)
        {
            return false;
        }

        return WebUiNative.SetPort(_id, (nuint)port) != 0;
    }

    private static bool IsServerUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _);

    internal static string RequireServerUrl(
        string? url,
        nuint windowId,
        Func<(nuint ErrorNumber, string? Message)> readNativeError)
    {
        if (IsServerUrl(url))
        {
            return url!;
        }

        var (errorNumber, nativeMessage) = readNativeError();
        var returned = string.IsNullOrWhiteSpace(url) ? "an empty URL" : $"the invalid URL '{url}'";
        var detail = string.IsNullOrWhiteSpace(nativeMessage)
            ? $"WebUI reported no error details (error {errorNumber})."
            : $"WebUI error {errorNumber}: {nativeMessage}";
        throw new WebUiException(
            errorNumber,
            $"WebUI could not start the server for window {windowId}; the native call returned {returned}. {detail}");
    }

    /// <summary>Closes the visible window but retains its native object and bindings.</summary>
    public void Close()
    {
        ThrowIfDisposed();
        WebUiNative.Close(_id);
    }

    /// <summary>Brings the window to the foreground.</summary>
    public void Focus()
    {
        ThrowIfDisposed();
        WebUiNative.Focus(_id);
    }

    /// <summary>Minimizes an embedded WebView window.</summary>
    public void Minimize()
    {
        ThrowIfDisposed();
        WebUiNative.Minimize(_id);
    }

    /// <summary>Maximizes an embedded WebView window.</summary>
    public void Maximize()
    {
        ThrowIfDisposed();
        WebUiNative.Maximize(_id);
    }

    /// <summary>Sets the root directory from which this window serves files.</summary>
    /// <exception cref="WebUiException">The native runtime rejected the path.</exception>
    public void SetRootFolder(string path)
    {
        if (!TrySetRootFolder(path))
        {
            throw WebUiApplication.CreateNativeException();
        }
    }

    /// <summary>Attempts to set the root directory from which this window serves files.</summary>
    public bool TrySetRootFolder(string path)
    {
        ThrowIfDisposed();
        var bytes = Utf8.Encode(path, nameof(path));
        fixed (byte* value = bytes)
        {
            return WebUiNative.SetRootFolder(_id, value) != 0;
        }
    }

    /// <summary>Sets a managed handler that returns complete raw HTTP responses for this window.</summary>
    /// <remarks>
    /// <para>
    /// WebUI supplies a cleaned, URL-decoded path. The handler is retained until it is replaced or
    /// the window is disposed. Response bytes are copied into WebUI-owned memory before this method
    /// returns to native code, so the handler does not need to pin or retain its response buffer.
    /// </para>
    /// <para>
    /// Returning <see cref="WebUiFileHandlerResult.NotHandled"/> allows WebUI to fall through to its
    /// configured local root. Return an explicit HTTP error response when fallback must be closed.
    /// Installing any custom file handler disables WebUI's authentication-cookie check process-wide;
    /// custom handlers are intended for private, loopback-only windows unless the application adds
    /// its own protection.
    /// </para>
    /// </remarks>
    public void SetFileHandler(WebUiFileHandler handler, WebUiFileHandlerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new WebUiFileHandlerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxResponseBytes, 1);
        var registration = new FileHandlerRegistration(handler, options.MaxResponseBytes);

        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            Volatile.Write(ref _fileHandler, registration);
            WebUiNative.SetFileHandlerWindow(_id, &FileHandlerTrampoline);
        }
    }

    /// <summary>Sets the server port that will be used when the window starts.</summary>
    /// <exception cref="WebUiException">The port cannot be reserved.</exception>
    public void SetPort(nuint port)
    {
        if (!TrySetPort(port))
        {
            throw WebUiApplication.CreateNativeException();
        }
    }

    /// <summary>Attempts to set the server port that will be used when the window starts.</summary>
    public bool TrySetPort(nuint port)
    {
        ThrowIfDisposed();
        if (WebUiNative.SetPort(_id, port) == 0)
        {
            return false;
        }

        Volatile.Write(ref _customPort, 1);
        return true;
    }

    /// <summary>Sets whether the window runs in kiosk mode.</summary>
    public void SetKiosk(bool enabled)
    {
        ThrowIfDisposed();
        WebUiNative.SetKiosk(_id, WebUiApplication.NativeBoolean(enabled));
    }

    /// <summary>Sets whether the window frame can be resized.</summary>
    public void SetResizable(bool enabled)
    {
        ThrowIfDisposed();
        WebUiNative.SetResizable(_id, WebUiApplication.NativeBoolean(enabled));
    }

    /// <summary>Sets whether callbacks for this window block the WebUI event thread.</summary>
    public void SetEventBlocking(bool enabled)
    {
        ThrowIfDisposed();
        WebUiNative.SetEventBlocking(_id, WebUiApplication.NativeBoolean(enabled));
    }

    /// <summary>Sets whether the native WebView uses a borderless window frame.</summary>
    public void SetFrameless(bool enabled)
    {
        ThrowIfDisposed();
        WebUiNative.SetFrameless(_id, WebUiApplication.NativeBoolean(enabled));
    }

    /// <summary>Sets whether the native WebView background is transparent.</summary>
    public void SetTransparent(bool enabled)
    {
        ThrowIfDisposed();
        WebUiNative.SetTransparent(_id, WebUiApplication.NativeBoolean(enabled));
    }

    /// <summary>Sets whether WebUI's high-contrast mode is enabled for this window.</summary>
    public void SetHighContrast(bool enabled)
    {
        ThrowIfDisposed();
        WebUiNative.SetHighContrast(_id, WebUiApplication.NativeBoolean(enabled));
    }

    /// <summary>Sets whether the browser window is hidden after it starts.</summary>
    public void SetHidden(bool enabled)
    {
        ThrowIfDisposed();
        WebUiNative.SetHide(_id, WebUiApplication.NativeBoolean(enabled));
    }

    /// <summary>Sets whether the window can be reached from the public network.</summary>
    public void SetPublic(bool enabled)
    {
        ThrowIfDisposed();
        WebUiNative.SetPublic(_id, WebUiApplication.NativeBoolean(enabled));
    }

    /// <summary>Sets the window's outer dimensions in pixels.</summary>
    public void SetSize(uint width, uint height)
    {
        ThrowIfDisposed();
        WebUiNative.SetSize(_id, width, height);
    }

    /// <summary>Sets the window's minimum dimensions in pixels.</summary>
    public void SetMinimumSize(uint width, uint height)
    {
        ThrowIfDisposed();
        WebUiNative.SetMinimumSize(_id, width, height);
    }

    /// <summary>Sets the window position in pixels.</summary>
    public void SetPosition(uint x, uint y)
    {
        ThrowIfDisposed();
        WebUiNative.SetPosition(_id, x, y);
    }

    /// <summary>Centers the window on the screen.</summary>
    public void Center()
    {
        ThrowIfDisposed();
        WebUiNative.SetCenter(_id);
    }

    /// <summary>Sets the browser profile name and storage path used for this window.</summary>
    public void SetProfile(string name, string path)
    {
        ThrowIfDisposed();
        var nameBytes = Utf8.Encode(name, nameof(name));
        var pathBytes = Utf8.Encode(path, nameof(path));
        fixed (byte* nativeName = nameBytes)
        fixed (byte* nativePath = pathBytes)
        {
            WebUiNative.SetProfile(_id, nativeName, nativePath);
        }
    }

    /// <summary>Sets the proxy server used for the browser window.</summary>
    public void SetProxy(string proxyServer)
    {
        ThrowIfDisposed();
        var bytes = Utf8.Encode(proxyServer, nameof(proxyServer));
        fixed (byte* value = bytes)
        {
            WebUiNative.SetProxy(_id, value);
        }
    }

    /// <summary>Sets extra command-line parameters passed to the launched browser.</summary>
    public void SetCustomParameters(string parameters)
    {
        ThrowIfDisposed();
        var bytes = Utf8.Encode(parameters, nameof(parameters));
        fixed (byte* value = bytes)
        {
            WebUiNative.SetCustomParameters(_id, value);
        }
    }

    /// <summary>Sets the icon served to browser clients for this window.</summary>
    /// <param name="icon">The textual icon data, such as an SVG document.</param>
    /// <param name="mimeType">The icon MIME type, such as <c>image/svg+xml</c>.</param>
    public void SetIcon(string icon, string mimeType)
    {
        ThrowIfDisposed();
        var iconBytes = Utf8.Encode(icon, nameof(icon));
        var mimeTypeBytes = Utf8.Encode(mimeType, nameof(mimeType));
        fixed (byte* iconValue = iconBytes)
        fixed (byte* mimeTypeValue = mimeTypeBytes)
        {
            WebUiNative.SetIcon(_id, iconValue, mimeTypeValue);
        }
    }

    /// <summary>Sets the icon file used by supported native window hosts.</summary>
    /// <remarks>WebUI currently uses this path for the Linux GTK taskbar icon.</remarks>
    public void SetIconFile(string path)
    {
        ThrowIfDisposed();
        var bytes = Utf8.Encode(path, nameof(path));
        fixed (byte* value = bytes)
        {
            WebUiNative.SetIconFile(_id, value);
        }
    }

    /// <summary>Sets the JavaScript runtime used for JavaScript or TypeScript files.</summary>
    public void SetRuntime(WebUiRuntime runtime)
    {
        ThrowIfDisposed();
        WebUiNative.SetRuntime(_id, (nuint)runtime);
    }

    /// <summary>Navigates every connected client to a URL.</summary>
    public void Navigate(string url)
    {
        ThrowIfDisposed();
        var bytes = Utf8.Encode(url, nameof(url));
        fixed (byte* value = bytes)
        {
            WebUiNative.Navigate(_id, value);
        }
    }

    /// <summary>Runs JavaScript in every connected client without waiting for a response.</summary>
    public void RunJavaScript(string script)
    {
        ThrowIfDisposed();
        var bytes = Utf8.Encode(script, nameof(script));
        fixed (byte* value = bytes)
        {
            WebUiNative.Run(_id, value);
        }
    }

    /// <summary>Runs JavaScript and returns its string result.</summary>
    /// <param name="script">The JavaScript expression to run.</param>
    /// <param name="timeout">The maximum native wait time. <see langword="null"/> uses WebUI's unlimited timeout.</param>
    /// <param name="responseBufferSize">Maximum UTF-8 response bytes, including the null terminator.</param>
    /// <exception cref="WebUiException">The script failed or timed out.</exception>
    public string ExecuteJavaScript(string script, TimeSpan? timeout = null, int responseBufferSize = 4 * 1024)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(responseBufferSize, 1);
        if (timeout is { } specifiedTimeout && specifiedTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var scriptBytes = Utf8.Encode(script, nameof(script));
        var responseBytes = GC.AllocateUninitializedArray<byte>(responseBufferSize);
        Array.Clear(responseBytes);
        var seconds = timeout is null ? 0u : checked((nuint)Math.Ceiling(timeout.Value.TotalSeconds));
        fixed (byte* nativeScript = scriptBytes)
        fixed (byte* nativeResponse = responseBytes)
        {
            if (WebUiNative.Script(_id, nativeScript, seconds, nativeResponse, (nuint)responseBytes.Length) == 0)
            {
                throw WebUiApplication.CreateNativeException();
            }
        }

        var terminator = Array.IndexOf(responseBytes, (byte)0);
        return Encoding.UTF8.GetString(responseBytes, 0, terminator < 0 ? responseBytes.Length : terminator);
    }

    /// <summary>Sends arbitrary bytes to every connected client.</summary>
    public void SendRaw(string function, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        var functionBytes = Utf8.Encode(function, nameof(function));
        fixed (byte* nativeFunction = functionBytes)
        fixed (byte* nativeData = data)
        {
            WebUiNative.SendRaw(_id, nativeFunction, nativeData, (nuint)data.Length);
        }
    }

    /// <summary>Destroys the native window once active managed callbacks have completed.</summary>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            {
                return;
            }

            _bindings.Clear();
        }

        try
        {
            _shutdown.Cancel();
        }
        finally
        {
            // A callback that ends meanwhile defers destruction to this call, so the token
            // source is not disposed while it is being cancelled.
            Interlocked.Exchange(ref _shutdownCancelled, 1);
            TryDestroy();
        }

        GC.SuppressFinalize(this);
    }

    internal void RemoveBinding(nuint id) => _bindings.TryRemove(id, out _);

    /// <summary>Gets whether the current thread is running a native binding callback.</summary>
    internal static bool IsDispatchingOnCurrentThread => t_dispatchDepth > 0;

    private static nuint CreateWithId(nuint windowId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowId);
        return WebUiNative.NewWindowId(windowId);
    }

    private WebUiBinding BindCore(string element, BindingRegistration registration)
    {
        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            var bytes = Utf8.Encode(element, nameof(element));
            nuint id;
            fixed (byte* value = bytes)
            {
                id = WebUiNative.Bind(_id, value, &CallbackTrampoline);
            }

            if (id == 0)
            {
                throw WebUiApplication.CreateNativeException();
            }

            if (!_bindings.TryAdd(id, registration))
            {
                throw new InvalidOperationException($"WebUI returned an already registered binding id {id}.");
            }

            return new WebUiBinding(this, id, element);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CallbackTrampoline(WebUiEventNative* nativeEvent)
    {
        if (nativeEvent is null)
        {
            return;
        }

        if (!Windows.TryGetValue(nativeEvent->Window, out var window))
        {
            CompleteRefusedEvent(nativeEvent);
            return;
        }

        window.Dispatch(nativeEvent);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* FileHandlerTrampoline(nuint windowId, byte* path, int* length)
    {
        if (length is null)
        {
            return null;
        }

        *length = 0;
        WebUiWindow? window = null;
        var callbackStarted = false;
        try
        {
            if (!Windows.TryGetValue(windowId, out window)
                || !window.TryBeginCallback())
            {
                CompleteNotHandledFileResponse(windowId);
                return null;
            }

            callbackStarted = true;
            WebUiFileHandlerResult result;
            try
            {
                var registration = Volatile.Read(ref window._fileHandler);
                result = registration is null
                    ? WebUiFileHandlerResult.NotHandled
                    : registration.Handler(Utf8.DecodeRequired(path));

                if (result.IsHandled && result.Response.Length > registration!.MaxResponseBytes)
                {
                    throw new InvalidDataException(
                        $"The file handler response exceeds the configured {registration.MaxResponseBytes} byte limit.");
                }
            }
            catch (Exception exception)
            {
                WebUiApplication.ReportUnhandledCallbackException(exception, window, null);
                result = WebUiFileHandlerResult.FromResponse(FileHandlerFailureResponse);
            }

            if (!result.IsHandled)
            {
                CompleteNotHandledFileResponse(windowId);
                return null;
            }

            return CopyFileResponseToNative(windowId, result.Response, length);
        }
        catch (Exception exception)
        {
            // No managed exception may cross the native HTTP callback boundary.
            if (window is not null)
            {
                WebUiApplication.ReportUnhandledCallbackException(exception, window, null);
            }

            try
            {
                return CopyFileResponseToNative(windowId, FileHandlerFailureResponse, length);
            }
            catch
            {
                CompleteNotHandledFileResponse(windowId);
                return null;
            }
        }
        finally
        {
            if (callbackStarted)
            {
                window!.EndCallback();
            }
        }
    }

    private static void* CopyFileResponseToNative(
        nuint windowId,
        ReadOnlyMemory<byte> response,
        int* length)
    {
        var responseLength = response.Length;
        void* nativeResponse = WebUiNative.Malloc((nuint)responseLength);
        if (nativeResponse is null)
        {
            throw new InvalidOperationException("WebUI could not allocate a file-response buffer.");
        }

        try
        {
            fixed (byte* source = response.Span)
            {
                WebUiNative.Memcpy(nativeResponse, source, (nuint)responseLength);
            }

            *length = responseLength;
            if (WebUiApplication.AsynchronousResponsesEnabled)
            {
                WebUiNative.InterfaceSetResponseFileHandler(windowId, nativeResponse, responseLength);
                return null;
            }

            return nativeResponse;
        }
        catch
        {
            WebUiNative.Free(nativeResponse);
            throw;
        }
    }

    private static void CompleteNotHandledFileResponse(nuint windowId)
    {
        if (WebUiApplication.AsynchronousResponsesEnabled)
        {
            WebUiNative.InterfaceSetResponseFileHandler(windowId, null, 0);
        }
    }

    private static void CompleteRefusedEvent(WebUiEventNative* nativeEvent)
    {
        // Without a reply, WebUI's thread for this call waits until the application exits, and
        // destroying the window waits for that thread. The event is still valid here, inside the
        // native callback.
        if (WebUiApplication.AsynchronousResponsesEnabled)
        {
            byte empty = 0;
            WebUiNative.InterfaceSetResponse(nativeEvent->Window, nativeEvent->EventNumber, &empty);
        }
    }

    private void Dispatch(WebUiEventNative* nativeEvent)
    {
        if (!TryBeginCallback())
        {
            CompleteRefusedEvent(nativeEvent);
            return;
        }

        WebUiEvent? webUiEvent = null;
        t_dispatchDepth++;
        try
        {
            webUiEvent = new WebUiEvent(this, nativeEvent);
            if (!_bindings.TryGetValue(webUiEvent.BindingId, out var registration))
            {
                webUiEvent.TryRespond(WebUiResult.None);
                CompleteCallback(webUiEvent);
                return;
            }

            var result = registration.Handler(webUiEvent, _shutdown.Token);
            if (registration.IsAsync && !result.IsCompletedSuccessfully)
            {
                _ = CompleteAsync(result, webUiEvent);
                return;
            }

            webUiEvent.TryRespond(result.GetAwaiter().GetResult());
            CompleteCallback(webUiEvent);
        }
        catch (Exception exception)
        {
            ReportCallbackFailure(exception, webUiEvent);
            CompleteCallback(webUiEvent);
        }
        finally
        {
            // The native callback returns now. Its event stays valid only while WebUI waits for
            // an asynchronous reply, which it stops doing when the application exits.
            webUiEvent?.EndDispatch();
            t_dispatchDepth--;
        }
    }

    private Task CompleteAsync(ValueTask<WebUiResult> operation, WebUiEvent webUiEvent)
        => WebUiAsyncCompletion.CompleteAsync(
            operation,
            webUiEvent,
            (exception, completedEvent) => ReportCallbackFailure(exception, completedEvent),
            completedEvent => CompleteCallback(completedEvent));

    private void ReportCallbackFailure(Exception exception, WebUiEvent? webUiEvent)
    {
        try
        {
            webUiEvent?.TryRespond(WebUiResult.None);
        }
        catch
        {
            // The original callback failure is more useful than a return failure.
        }

        WebUiApplication.ReportUnhandledCallbackException(exception, this, webUiEvent);
    }

    private bool TryBeginCallback()
    {
        while (true)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                return false;
            }

            var count = Volatile.Read(ref _callbacksInFlight);
            if (Interlocked.CompareExchange(ref _callbacksInFlight, count + 1, count) != count)
            {
                continue;
            }

            if (Volatile.Read(ref _disposeRequested) == 0)
            {
                return true;
            }

            EndCallback();
            return false;
        }
    }

    private void CompleteCallback(WebUiEvent? webUiEvent)
    {
        try
        {
            webUiEvent?.Invalidate();
        }
        finally
        {
            EndCallback();
        }
    }

    private void EndCallback()
    {
        if (Interlocked.Decrement(ref _callbacksInFlight) == 0)
        {
            TryDestroy();
        }
    }

    private void TryDestroy()
    {
        if (Volatile.Read(ref _disposeRequested) == 0
            || Volatile.Read(ref _shutdownCancelled) == 0
            || Volatile.Read(ref _callbacksInFlight) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _destroyed, 1, 0) != 0)
        {
            return;
        }

        Windows.TryRemove(new KeyValuePair<nuint, WebUiWindow>(_id, this));
        WebUiNativeLifetime.Destroy(_id, _generation);

        Volatile.Write(ref _fileHandler, null);
        _shutdown.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
    }

    private sealed class BindingRegistration
    {
        internal BindingRegistration(Func<WebUiEvent, CancellationToken, ValueTask<WebUiResult>> handler, bool isAsync)
        {
            Handler = handler;
            IsAsync = isAsync;
        }

        internal Func<WebUiEvent, CancellationToken, ValueTask<WebUiResult>> Handler { get; }

        internal bool IsAsync { get; }
    }

    private sealed class FileHandlerRegistration
    {
        internal FileHandlerRegistration(WebUiFileHandler handler, int maxResponseBytes)
        {
            Handler = handler;
            MaxResponseBytes = maxResponseBytes;
        }

        internal WebUiFileHandler Handler { get; }

        internal int MaxResponseBytes { get; }
    }
}
