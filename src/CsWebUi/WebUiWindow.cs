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

    // Ports tried before giving up on finding one that is free on both loopback addresses.
    private const int PortSelectionAttempts = 16;

    // How long a destroyed window keeps holding [::1] while WebUI's server still listens.
    private static readonly TimeSpan LoopbackReleaseLimit = TimeSpan.FromSeconds(30);

    // What webui_get_url shows when it has to start the server itself.
    private static readonly byte[] ServerOnlyPage = Encoding.ASCII.GetBytes(
        "<html><head><script src=\"webui.js\"></script></head></html>\0");

    // Keyed by generation too: WebUI reuses window ids after WebUiApplication.Clean, while a window
    // whose disposal a pending callback deferred can still be registered under its old id.
    private static readonly ConcurrentDictionary<(int Generation, nuint Id), WebUiWindow> Windows = new();
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
    private readonly int _generation;
    private FileHandlerRegistration? _fileHandler;
    private WebUiLoopbackGuard? _loopbackGuard;
    private int _callbacksInFlight;
    private int _customPort;
    private int _public;
    private int _serverRequested;
    private int _disposeRequested;
    private int _shutdownCancelled;
    private int _destroyed;

    /// <summary>Creates a new WebUI window with an automatically allocated identifier.</summary>
    /// <exception cref="WebUiException">The native runtime could not allocate the window.</exception>
    public WebUiWindow()
        : this(static () => WebUiNative.NewWindow())
    {
    }

    /// <summary>Creates a new WebUI window with a caller-selected identifier.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowId"/> is zero.</exception>
    /// <exception cref="WebUiException">The requested identifier is already in use or could not be allocated.</exception>
    public WebUiWindow(nuint windowId)
        : this(CreateWithId(windowId))
    {
    }

    private WebUiWindow(Func<nuint> create)
    {
        // Created atomically with WebUiApplication.Clean, so the generation matches the native window.
        var id = WebUiNativeLifetime.CreateWindow(create, out _generation);
        if (id == 0)
        {
            throw WebUiApplication.CreateNativeException();
        }

        _id = id;
        if (!Windows.TryAdd((_generation, id), this))
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

    /// <summary>Gets the URL of this window's local server.</summary>
    /// <remarks>
    /// <para>
    /// WebUI starts the window's server when its URL is read before the window is shown. CS-WebUI
    /// keeps that behaviour, but starts the server itself first, on a port it chose the same way
    /// as <see cref="Show"/> does, so the server is protected from the start.
    /// </para>
    /// <para>
    /// The URL is <c>http://localhost:{port}</c>, the origin the window's pages use. After
    /// <see cref="SetPublic"/> with <see langword="true"/> it is WebUI's public URL, which names
    /// the address of the first network interface instead.
    /// </para>
    /// </remarks>
    /// <value>
    /// The URL, or <see langword="null"/> when the server could not be started. The reason is
    /// reported to the logger set with <see cref="WebUiApplication.SetLogger"/> and to
    /// <see cref="System.Diagnostics.Trace"/>.
    /// </value>
    public string? Url
    {
        get
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _serverRequested) == 0 && !TryStartServerForUrl())
            {
                return null;
            }

            if (Volatile.Read(ref _public) != 0)
            {
                return Utf8.Decode(WebUiNative.GetUrl(_id));
            }

            // webui_get_url names the first network interface's address, where WebUI does not
            // listen unless the window is public.
            var port = WebUiNative.GetPort(_id);
            return port == 0
                ? null
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"http://localhost:{port}");
        }
    }

    // webui_get_url would start the server through WebUI's own port choice, which checks
    // 127.0.0.1 only. Start it here the same way webui_get_url does, after choosing the port.
    private bool TryStartServerForUrl()
    {
        if (!TryReserveServerPort())
        {
            return false;
        }

        fixed (byte* page = ServerOnlyPage)
        {
            if (MarkServerStarted(WebUiNative.ShowBrowser(_id, page, (nuint)WebUiBrowser.NoBrowser) != 0))
            {
                return true;
            }
        }

        WebUiApplication.ReportProblem($"WebUI could not start the server of window {_id}, so it has no URL yet.");
        return false;
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
    /// <exception cref="WebUiException">
    /// CS-WebUI found no port that is free on both <c>127.0.0.1</c> and <c>[::1]</c>; see
    /// <see cref="StartServer"/>. Also thrown when <c>CSWEBUI_BROWSER_PATH</c> names a browser that
    /// cannot be used.
    /// </exception>
    public void Show(string content)
    {
        ThrowIfDisposed();
        if (!WebUiApplication.TryGetConfiguredBrowser(out var configured, out var error))
        {
            throw new WebUiException(0, error);
        }

        var requested = configured?.Browser ?? WebUiBrowser.AnyBrowser;
        ReserveServerPort();
        var started = Stopwatch.GetTimestamp();
        if (!ShowWith(content, configured, requested))
        {
            throw CreateShowException(requested, Stopwatch.GetElapsedTime(started), configured);
        }
    }

    /// <summary>Attempts to show HTML content, a local file, or a URL using WebUI's recommended browser.</summary>
    /// <remarks>
    /// Honors <see cref="WebUiApplication.SetBrowserPath"/> and <c>CSWEBUI_BROWSER_PATH</c> like
    /// <see cref="Show"/>. When the environment variable names a browser that cannot be used, this
    /// returns <see langword="false"/> and reports the reason to the logger set with
    /// <see cref="WebUiApplication.SetLogger"/> and to <see cref="System.Diagnostics.Trace"/>.
    /// </remarks>
    public bool TryShow(string content)
    {
        ThrowIfDisposed();
        if (!WebUiApplication.TryGetConfiguredBrowser(out var configured, out var error))
        {
            WebUiApplication.ReportProblem(error);
            return false;
        }

        return TryReserveServerPort()
            && ShowWith(content, configured, configured?.Browser ?? WebUiBrowser.AnyBrowser);
    }

    /// <summary>Shows HTML content, a local file, or a URL in a selected browser.</summary>
    /// <remarks>
    /// When <paramref name="browser"/> is the kind selected with
    /// <see cref="WebUiApplication.SetBrowserPath"/> or <c>CSWEBUI_BROWSER_PATH</c>, that executable is used.
    /// </remarks>
    /// <exception cref="WebUiShowException">WebUI could not show the window in that browser.</exception>
    /// <exception cref="WebUiException">
    /// CS-WebUI found no port that is free on both <c>127.0.0.1</c> and <c>[::1]</c>; see
    /// <see cref="StartServer"/>. Also thrown when <c>CSWEBUI_BROWSER_PATH</c> names a browser that
    /// cannot be used.
    /// </exception>
    public void ShowInBrowser(string content, WebUiBrowser browser)
    {
        ThrowIfDisposed();
        if (!WebUiApplication.TryGetConfiguredBrowser(out var configured, out var error))
        {
            throw new WebUiException(0, error);
        }

        var matching = configured is not null && configured.Browser == browser ? configured : null;
        ReserveServerPort();
        var started = Stopwatch.GetTimestamp();
        if (!ShowWith(content, matching, browser))
        {
            throw CreateShowException(browser, Stopwatch.GetElapsedTime(started), matching);
        }
    }

    /// <summary>Attempts to show HTML content, a local file, or a URL in a selected browser.</summary>
    /// <remarks>Reports an unusable <c>CSWEBUI_BROWSER_PATH</c> like <see cref="TryShow"/>.</remarks>
    public bool TryShowInBrowser(string content, WebUiBrowser browser)
    {
        ThrowIfDisposed();
        if (!WebUiApplication.TryGetConfiguredBrowser(out var configured, out var error))
        {
            WebUiApplication.ReportProblem(error);
            return false;
        }

        return TryReserveServerPort()
            && ShowWith(content, configured is not null && configured.Browser == browser ? configured : null, browser);
    }

    /// <summary>Shows HTML content, a local file, or a URL in an embedded WebView.</summary>
    /// <exception cref="WebUiShowException">The native runtime could not show the WebView.</exception>
    /// <exception cref="WebUiException">
    /// CS-WebUI found no port that is free on both <c>127.0.0.1</c> and <c>[::1]</c>; see
    /// <see cref="StartServer"/>.
    /// </exception>
    public void ShowWebView(string content)
    {
        ThrowIfDisposed();
        ReserveServerPort();
        var started = Stopwatch.GetTimestamp();
        if (!ShowWebViewCore(content))
        {
            throw CreateShowException(WebUiBrowser.WebView, Stopwatch.GetElapsedTime(started), null);
        }
    }

    /// <summary>Attempts to show HTML content, a local file, or a URL in an embedded WebView.</summary>
    public bool TryShowWebView(string content)
    {
        ThrowIfDisposed();
        return TryReserveServerPort() && ShowWebViewCore(content);
    }

    private bool ShowWebViewCore(string content)
    {
        var bytes = Utf8.Encode(content, nameof(content));
        fixed (byte* value = bytes)
        {
            return MarkServerStarted(WebUiNative.ShowWebView(_id, value) != 0);
        }
    }

    private bool ShowWith(string content, ConfiguredBrowser? configured, WebUiBrowser browser)
    {
        var bytes = Utf8.Encode(content, nameof(content));
        if (configured is not null)
        {
            return WebUiApplication.WithBrowserFolder(configured, () => ShowNative(bytes, browser));
        }

        return ShowNative(bytes, browser);
    }

    private bool ShowNative(byte[] content, WebUiBrowser browser)
    {
        fixed (byte* value = content)
        {
            var shown = browser == WebUiBrowser.AnyBrowser
                ? WebUiNative.Show(_id, value) != 0
                : WebUiNative.ShowBrowser(_id, value, (nuint)browser) != 0;
            return MarkServerStarted(shown);
        }
    }

    // A later StartServer must not move a running server to another port, but after a failed
    // show it may still pick a port itself.
    private bool MarkServerStarted(bool started)
    {
        if (started)
        {
            Volatile.Write(ref _serverRequested, 1);
        }

        return started;
    }

    private WebUiShowException CreateShowException(WebUiBrowser requested, TimeSpan elapsed, ConfiguredBrowser? configured)
    {
        var tried = requested;
        if (requested == WebUiBrowser.AnyBrowser)
        {
            // The same choice WebUI made: a running browser first, then its preference order.
            tried = (WebUiBrowser)(uint)WebUiNative.GetBestBrowser(_id);
        }

        string? path = null;
        if (tried is not (WebUiBrowser.AnyBrowser or WebUiBrowser.ChromiumBased or WebUiBrowser.WebView))
        {
            // WebUI takes the first of its names that exists in the folder, which may not be the
            // configured file itself.
            path = WebUiBrowserLocator.Find(tried, configured?.Folder ?? WebUiApplication.BrowserFolder);
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
            // WebUI waits the whole timeout for a started browser; a launch failure returns at once.
            if (elapsed >= connectionTimeout * 0.9)
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
            message.Append(" No display is available: DISPLAY and WAYLAND_DISPLAY are not set, and there is no default Wayland socket.");
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
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            return true;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return true;
        }

        // libwayland connects to $XDG_RUNTIME_DIR/wayland-0 when WAYLAND_DISPLAY is unset.
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtimeDirectory) && File.Exists(Path.Combine(runtimeDirectory, "wayland-0"));
    }

    /// <summary>Starts the local WebUI server without launching a browser.</summary>
    /// <returns>The absolute URL of the started server.</returns>
    /// <remarks>
    /// <para>
    /// Unless <see cref="SetPort"/> chose a port, the first start takes a free port from the
    /// operating system and retries a few times with a new one when listening fails. WebUI's own
    /// choice repeats across processes started in the same second, so concurrent hosts collided.
    /// WebUI also resets <see cref="WebUiApplication.SetConnectionTimeout"/> to zero here.
    /// </para>
    /// <para>
    /// The port must be free on both <c>127.0.0.1</c> and <c>[::1]</c>, because browsers open the
    /// window's <c>localhost</c> URL on either. CS-WebUI tries up to 16 ports and then fails
    /// rather than use a port it could not check; the same applies to the <c>Show</c> methods and
    /// <see cref="Url"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="WebUiException">
    /// The native runtime could not start the server. WebUI reports this by returning an empty URL,
    /// for example when the window's server did not start listening in time. The failed call can
    /// be retried; WebUI then selects a new port. Also thrown when no port free on both loopback
    /// addresses was found, for example because another local process holds the ports tried.
    /// </exception>
    public string StartServer(string content)
    {
        ThrowIfDisposed();
        var bytes = Utf8.Encode(content, nameof(content));
        for (var attempt = 1; ; attempt++)
        {
            var selectedPort = MustSelectPort() ? SelectFreePort() : 0;
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

            // Retry only a collision on the port this method chose: not after Exit, and not when the
            // port is still free, which means the failure had another cause.
            if (selectedPort == 0
                || attempt == StartServerAttempts
                || WebUiNativeLifetime.IsExiting
                || !IsPortTaken(selectedPort))
            {
                if (selectedPort != 0)
                {
                    ReplaceLoopbackGuard(null);
                }

                return RequireServerUrl(result, _id, WebUiApplication.ReadNativeError);
            }
        }
    }

    // Until WebUI's server is running, CS-WebUI picks its port unless SetPort chose one.
    private bool MustSelectPort()
        => Volatile.Read(ref _customPort) == 0 && Volatile.Read(ref _serverRequested) == 0;

    private void ReserveServerPort()
    {
        if (MustSelectPort())
        {
            SelectFreePort();
        }
    }

    private bool TryReserveServerPort()
    {
        try
        {
            ReserveServerPort();
            return true;
        }
        catch (WebUiException exception)
        {
            WebUiApplication.ReportProblem(exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Gives WebUI a port that is free on both loopback addresses, and holds its <c>[::1]</c> side.
    /// </summary>
    /// <returns>The port.</returns>
    /// <exception cref="WebUiException">No such port could be found and set.</exception>
    private int SelectFreePort()
    {
        // WebUI draws ports from rand() seeded with the current second, so processes started in
        // the same second try the same ports and all but one fail to listen (#18). The operating
        // system hands out a distinct free port instead.
        //
        // WebUI listens on 127.0.0.1 only but opens http://localhost:port, which browsers try on
        // [::1] first. The port must be free there as well, and stays held there (see
        // WebUiLoopbackGuard), or another local process could serve the window's page.
        for (var attempt = 0; attempt < PortSelectionAttempts; attempt++)
        {
            // Alternate which family picks, so ports taken on one family alone cannot use up
            // every attempt.
            var port = TryReserveLoopbackPort(ipv6First: attempt % 2 == 1, out var guard);
            if (port == 0)
            {
                continue;
            }

            if (WebUiNative.SetPort(_id, (nuint)port) != 0)
            {
                ReplaceLoopbackGuard(guard);
                return port;
            }

            guard?.Dispose();
        }

        // Fail closed: WebUI's own choice checks 127.0.0.1 only.
        throw new WebUiException(
            0,
            $"CS-WebUI could not find a port for window {_id} that is free on both 127.0.0.1 and [::1] after {PortSelectionAttempts} attempts. Another local process may be holding the ports it tried; close it or set a known free port with SetPort.");
    }

    // Offers the port that the family picking first tries (the argument is true when IPv6 picks)
    // instead of letting the operating system choose; tests use it to offer ports another socket
    // holds. Zero lets the operating system choose.
    internal static Func<bool, int>? PortCandidateSource { get; set; }

    /// <summary>
    /// Finds a port free on both loopback addresses: one family picks it, the other must accept it.
    /// </summary>
    /// <returns>The port with its <c>[::1]</c> side held by <paramref name="guard"/>, or zero.</returns>
    internal static int TryReserveLoopbackPort(bool ipv6First, out WebUiLoopbackGuard? guard)
    {
        guard = null;
        try
        {
            if (ipv6First)
            {
                if (!WebUiLoopbackGuard.TryCreate(PortCandidateSource?.Invoke(true) ?? 0, out guard))
                {
                    return 0;
                }

                if (guard is not null)
                {
                    if (!IsPortTaken(guard.Port))
                    {
                        return guard.Port;
                    }

                    guard.Dispose();
                    guard = null;
                    return 0;
                }

                // No IPv6 loopback: let IPv4 pick.
            }

            using var listener = new TcpListener(IPAddress.Loopback, PortCandidateSource?.Invoke(false) ?? 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!WebUiLoopbackGuard.TryCreate(port, out guard))
            {
                return 0;
            }

            listener.Stop();
            return port;
        }
        catch (SocketException)
        {
            guard?.Dispose();
            guard = null;
            return 0;
        }
    }

    private void ReplaceLoopbackGuard(WebUiLoopbackGuard? guard)
        => Interlocked.Exchange(ref _loopbackGuard, guard)?.Dispose();

    /// <summary>Gets the port whose <c>[::1]</c> address this window holds, or zero.</summary>
    internal int GuardedPort => Volatile.Read(ref _loopbackGuard)?.Port ?? 0;

    /// <summary>Completes when a destroyed window has stopped holding <c>[::1]</c>.</summary>
    internal Task LoopbackReleased { get; private set; } = Task.CompletedTask;

    internal static bool IsPortTaken(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
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
    /// <remarks>
    /// <para>
    /// The port must be free on both <c>127.0.0.1</c> and <c>[::1]</c>, because browsers open
    /// WebUI's <c>localhost</c> URL on either. The window holds <c>[::1]:{port}</c> from this call
    /// until it is disposed.
    /// </para>
    /// <para>
    /// Zero, before the server starts, discards an earlier choice: CS-WebUI then chooses the port
    /// when the window starts.
    /// </para>
    /// <para>
    /// Once the server runs, after <see cref="StartServer"/>, a successful <c>Show</c> method or
    /// reading <see cref="Url"/>, its port cannot change. WebUI would move the server
    /// asynchronously, and the old port could not stay protected while it does. Create a new
    /// window to use another port.
    /// </para>
    /// </remarks>
    /// <exception cref="WebUiException">The port cannot be reserved.</exception>
    /// <exception cref="InvalidOperationException">The window's server is already running.</exception>
    public void SetPort(nuint port)
    {
        switch (SetPortCore(port, out var error))
        {
            case SetPortOutcome.Set:
                return;
            case SetPortOutcome.ServerRunning:
                throw new InvalidOperationException(
                    $"The server of window {_id} is already running on port {WebUiNative.GetPort(_id)}; its port cannot change. Call SetPort before StartServer, Show or Url, or use a new window.");
            case SetPortOutcome.IPv6LoopbackTaken:
                throw new WebUiException(
                    0,
                    $"Port {port} cannot be used for window {_id}: another socket holds [::1]:{port}, where browsers would open the window's localhost URL.");
            case SetPortOutcome.SocketFailed:
                throw new WebUiException(
                    0,
                    $"Port {port} cannot be used for window {_id}: CS-WebUI could not hold [::1]:{port} ({error!.SocketErrorCode}: {error.Message}).");
            default:
                throw WebUiApplication.CreateNativeException();
        }
    }

    /// <summary>Attempts to set the server port that will be used when the window starts.</summary>
    /// <remarks>
    /// Behaves like <see cref="SetPort"/>, but returns <see langword="false"/> instead of throwing
    /// when the port is taken on <c>127.0.0.1</c> or <c>[::1]</c>, cannot be held, or the server is
    /// already running.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    public bool TrySetPort(nuint port) => SetPortCore(port, out _) == SetPortOutcome.Set;

    private SetPortOutcome SetPortCore(nuint port, out SocketException? error)
    {
        ThrowIfDisposed();
        error = null;
        if (Volatile.Read(ref _serverRequested) != 0)
        {
            // webui_set_port on a running server only requests a reload that the server thread
            // performs later, while pages still use the old port.
            return SetPortOutcome.ServerRunning;
        }

        if (port == 0)
        {
            if (WebUiNative.SetPort(_id, 0) == 0)
            {
                return SetPortOutcome.Failed;
            }

            Volatile.Write(ref _customPort, 0);
            ReplaceLoopbackGuard(null);
            return SetPortOutcome.Set;
        }

        if (port > IPEndPoint.MaxPort)
        {
            return SetPortOutcome.Failed;
        }

        // Keep holding [::1] when the window already holds this port.
        WebUiLoopbackGuard? guard = null;
        var keep = GuardedPort == (int)port;
        try
        {
            if (!keep && !WebUiLoopbackGuard.TryCreate((int)port, out guard))
            {
                return SetPortOutcome.IPv6LoopbackTaken;
            }
        }
        catch (SocketException exception)
        {
            error = exception;
            return SetPortOutcome.SocketFailed;
        }

        if (WebUiNative.SetPort(_id, port) == 0)
        {
            guard?.Dispose();
            return SetPortOutcome.Failed;
        }

        if (!keep)
        {
            ReplaceLoopbackGuard(guard);
        }

        Volatile.Write(ref _customPort, 1);
        return SetPortOutcome.Set;
    }

    private enum SetPortOutcome
    {
        Set,
        Failed,
        IPv6LoopbackTaken,
        SocketFailed,
        ServerRunning,
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
        Volatile.Write(ref _public, enabled ? 1 : 0);
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
    /// <remarks>
    /// <para>
    /// The result ends at its first null character, because WebUI copies it as a C string. A
    /// result that does not fit is cut to <paramref name="responseBufferSize"/> - 1 bytes (to
    /// <paramref name="responseBufferSize"/> bytes by WebUI builds before 2456744e6).
    /// </para>
    /// <para>
    /// WebUI builds before 2456744e6 keep the response buffer registered after their wait ends and
    /// copy a result that arrives later into it. So when a call with a finite
    /// <paramref name="timeout"/> fails, its buffer (<paramref name="responseBufferSize"/> bytes of
    /// native memory) is kept until <see cref="WebUiApplication.Clean"/> releases WebUI's state;
    /// each such call keeps one.
    /// </para>
    /// </remarks>
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
        var seconds = timeout is null ? 0u : checked((nuint)Math.Ceiling(timeout.Value.TotalSeconds));

        // Before 2456744e6, webui_script() kept the buffer registered when its wait ended and copied
        // a result that arrived later into it. A managed array would be unpinned by then, so use
        // native memory. Since 2456744e6 (webui-dev/webui#722) WebUI releases the buffer under its
        // lock before returning, so no write follows the return; the native buffer is redundant
        // with that WebUI but kept for any WebUI loaded through CSWEBUI_NATIVE_LIBRARY.
        var response = (byte*)NativeMemory.AllocZeroed((nuint)responseBufferSize);
        var release = true;
        try
        {
            bool succeeded;
            fixed (byte* nativeScript = scriptBytes)
            {
                succeeded = WebUiNative.Script(_id, nativeScript, seconds, response, (nuint)responseBufferSize) != 0;
            }

            if (!succeeded)
            {
                // WebUI waits without limit for 0 or more than 86400 seconds, so then it failed
                // without waiting or after the result arrived. A finite wait may have timed out,
                // and WebUI times it with its own clock, so keep the buffer for a late result.
                // Redundant since 2456744e6, which never writes after returning; kept until no
                // supported WebUI predates it.
                if (seconds is >= 1 and <= 86400)
                {
                    release = false;
                    WebUiNativeLifetime.RetainUntilClean(response);
                }

                throw WebUiApplication.CreateNativeException();
            }

            var result = new ReadOnlySpan<byte>(response, responseBufferSize);
            var terminator = result.IndexOf((byte)0);
            return Encoding.UTF8.GetString(terminator < 0 ? result : result[..terminator]);
        }
        finally
        {
            if (release)
            {
                NativeMemory.Free(response);
            }
        }
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

    private static Func<nuint> CreateWithId(nuint windowId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowId);
        return () => WebUiNative.NewWindowId(windowId);
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

        if (!Windows.TryGetValue((WebUiNativeLifetime.Generation, nativeEvent->Window), out var window))
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
        t_dispatchDepth++;
        WebUiNativeLifetime.EnterDispatch();
        try
        {
            if (!Windows.TryGetValue((WebUiNativeLifetime.Generation, windowId), out window)
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

            WebUiNativeLifetime.LeaveDispatch();
            t_dispatchDepth--;
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
        WebUiNativeLifetime.EnterDispatch();
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
            WebUiNativeLifetime.LeaveDispatch();
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

        Windows.TryRemove(new KeyValuePair<(int, nuint), WebUiWindow>((_generation, _id), this));
        WebUiNativeLifetime.Destroy(_id, _generation);

        // webui_destroy returns before the server has stopped when it runs inside one of the
        // window's callbacks, and after its timeouts. [::1] stays held until 127.0.0.1 is free.
        var guard = Interlocked.Exchange(ref _loopbackGuard, null);
        if (guard is not null)
        {
            LoopbackReleased = guard.DisposeWhenServerStopped(LoopbackReleaseLimit);
        }

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
