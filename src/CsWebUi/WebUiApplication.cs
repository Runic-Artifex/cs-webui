using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CsWebUi.Internal;
using CsWebUi.Native;

namespace CsWebUi;

/// <summary>Provides process-wide WebUI configuration and lifetime operations.</summary>
public static class WebUiApplication
{
    /// <summary>The environment variable that selects the browser executable when <see cref="SetBrowserPath"/> was not called.</summary>
    public const string BrowserPathEnvironmentVariable = "CSWEBUI_BROWSER_PATH";

    // WebUI's connection wait when no positive timeout is configured (WEBUI_DEF_TIMEOUT), and the
    // largest timeout it accepts (WEBUI_MAX_TIMEOUT).
    private const int DefaultConnectionTimeoutSeconds = 15;
    private const int MaximumConnectionTimeoutSeconds = 60;

    private static readonly TimeSpan AsyncWaitPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly object Gate = new();
    private static readonly object BrowserFolderGate = new();
    private static Action<WebUiLogLevel, string>? _logger;
    private static int _asynchronousResponsesEnabled;
    private static long _connectionTimeoutSeconds;
    private static string? _browserFolder;
    private static bool _browserPathSet;
    private static ConfiguredBrowser? _browserPath;

    /// <summary>Occurs when a managed binding throws before its response is sent.</summary>
    public static event EventHandler<WebUiUnhandledCallbackExceptionEventArgs>? UnhandledCallbackException;

    /// <summary>Gets whether the native WebUI application is still running.</summary>
    public static bool IsRunning => WebUiNative.InterfaceIsAppRunning() != 0;

    /// <summary>Sets a process-wide WebUI configuration option.</summary>
    public static void SetConfiguration(WebUiConfiguration configuration, bool enabled)
    {
        WebUiNative.SetConfig((CsWebUi.Native.WebUiConfig)configuration, NativeBoolean(enabled));
        if (configuration == WebUiConfiguration.AsynchronousResponse)
        {
            Volatile.Write(ref _asynchronousResponsesEnabled, enabled ? 1 : 0);
        }
    }

    /// <summary>Sets the maximum number of seconds WebUI waits for a client to connect.</summary>
    /// <remarks>
    /// WebUI also waits this long for a browser to connect when a window is shown; zero waits
    /// forever for a connection in <see cref="Wait"/> and 15 seconds when showing. WebUI caps the
    /// value at 60 seconds and ignores it after <see cref="Exit"/>.
    /// <see cref="WebUiWindow.StartServer"/> resets it to zero.
    /// </remarks>
    public static void SetConnectionTimeout(nuint seconds)
    {
        WebUiNative.SetTimeout(seconds);
        if (!WebUiNativeLifetime.IsExiting)
        {
            Volatile.Write(ref _connectionTimeoutSeconds, (long)Math.Min(seconds, (nuint)MaximumConnectionTimeoutSeconds));
        }
    }

    /// <summary>Sets the folder in which WebUI first searches for browser executables.</summary>
    /// <remarks>
    /// <para>
    /// WebUI looks in this folder for its known executable names, such as <c>chromium</c> or
    /// <c>google-chrome</c> on Linux and <c>chrome.exe</c> on Windows, before its normal search.
    /// It still chooses the browser kind in its own order, so a Chrome on <c>PATH</c> is preferred
    /// over a Chromium in this folder. Use <see cref="SetBrowserPath"/> to choose one browser.
    /// An empty path removes the folder.
    /// </para>
    /// <para>
    /// WebUI starts a browser it finds there through the shell (<c>system()</c>), with the path in
    /// double quotes, so the same characters as for <see cref="SetBrowserPath"/> are rejected.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The path contains a null or other control character, a double quote, or a character the
    /// shell expands inside double quotes.
    /// </exception>
    public static void SetBrowserFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (FindShellUnsafeCharacter(path) is { } unsafeCharacter)
        {
            throw new ArgumentException(
                $"The browser folder contains {unsafeCharacter}, which is unsafe in the shell command WebUI runs to start the browser.",
                nameof(path));
        }

        lock (BrowserFolderGate)
        {
            SetNativeBrowserFolder(path);
            Volatile.Write(ref _browserFolder, path.Length == 0 ? null : path);
        }
    }

    /// <summary>Selects the browser executable that <see cref="WebUiWindow.Show"/> launches.</summary>
    /// <param name="path">
    /// The browser executable (on macOS the <c>.app</c> bundle), or <see langword="null"/> to fall
    /// back to <see cref="BrowserPathEnvironmentVariable"/> and then to WebUI's own choice.
    /// </param>
    /// <remarks>
    /// <para>
    /// WebUI has no option for an executable path. CS-WebUI maps the path onto WebUI's browser
    /// folder and browser kind, so the file name must be one WebUI knows, for example
    /// <c>chromium</c>, <c>google-chrome</c>, <c>firefox</c> or <c>microsoft-edge-stable</c> on Linux,
    /// <c>chrome.exe</c> or <c>msedge.exe</c> on Windows, and <c>Chromium.app</c> on macOS. A
    /// wrapper script with one of these names works. <see cref="WebUiWindow.Show"/> then uses only
    /// this browser and does not fall back to another browser or the WebView.
    /// </para>
    /// <para>
    /// WebUI takes the first of its known names for that browser that exists in the folder, so a
    /// folder with both <c>chromium-browser</c> and <c>chromium</c> launches <c>chromium-browser</c>.
    /// The folder is set only while a window is being shown and then restored to the one from
    /// <see cref="SetBrowserFolder"/>, if any. WebUI runs the browser through the shell
    /// (<c>system()</c>) with the path in double quotes, so paths containing quotes, control
    /// characters, <c>$</c>, <c>`</c> or <c>\</c> (and <c>%</c> on Windows) are rejected.
    /// </para>
    /// <para>
    /// Without this call, the <see cref="BrowserPathEnvironmentVariable"/> environment variable
    /// (<c>CSWEBUI_BROWSER_PATH</c>) is used the same way. WebUI itself does not read
    /// <c>WEBUI_BROWSER_PATH</c> or any other browser environment variable.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The file does not exist, is not a browser WebUI can launch, or contains characters that are
    /// unsafe in a shell command.
    /// </exception>
    public static void SetBrowserPath(string? path)
    {
        ConfiguredBrowser? configured = null;
        if (path is not null && !TryResolveBrowserPath(path, out configured, out var error))
        {
            throw new ArgumentException(error, nameof(path));
        }

        lock (Gate)
        {
            _browserPathSet = path is not null;
            _browserPath = configured;
        }
    }

    /// <summary>Sets the default root folder used for subsequently created windows.</summary>
    /// <exception cref="WebUiException">The native runtime rejected the folder.</exception>
    public static unsafe void SetDefaultRootFolder(string path)
    {
        var bytes = Utf8.Encode(path, nameof(path));
        fixed (byte* value = bytes)
        {
            if (WebUiNative.SetDefaultRootFolder(value) == 0)
            {
                throw CreateNativeException();
            }
        }
    }

    /// <summary>Configures a managed target for native WebUI log records.</summary>
    public static unsafe void SetLogger(Action<WebUiLogLevel, string>? logger)
    {
        lock (Gate)
        {
            _logger = logger;
            WebUiNative.SetLogger(logger is null ? null : &LoggerTrampoline, null);
        }
    }

    /// <summary>Blocks until all shown WebUI windows have closed.</summary>
    public static void Wait() => WebUiNative.Wait();

    /// <summary>Asynchronously waits until all shown WebUI windows have closed.</summary>
    /// <remarks>
    /// WebUI exposes a non-blocking status check rather than a completion callback. This method
    /// polls that status at a short fixed interval so it does not occupy a worker thread.
    /// </remarks>
    public static async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WebUiNative.WaitAsync() == 0)
            {
                return;
            }

            await Task.Delay(AsyncWaitPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Requests all WebUI windows to close.</summary>
    /// <remarks>
    /// <para>
    /// WebUI stops waiting for replies that asynchronous bindings have not sent yet, and answers
    /// those calls with an empty result. From this call until <see cref="Clean"/>, a reply that an
    /// asynchronous binding completes later is discarded, and <see cref="WebUiEvent"/> members that
    /// reach the client throw <see cref="InvalidOperationException"/> outside the binding's
    /// synchronous part.
    /// </para>
    /// <para>
    /// Calling the raw <c>WebUiNative.Exit</c> directly bypasses this protection. WebUI also exits by
    /// itself after an internal failure (<c>_webui_panic</c>, for example when an allocation fails);
    /// such an exit is not seen by CS-WebUI, and a later asynchronous reply is then unprotected.
    /// </para>
    /// </remarks>
    public static void Exit() => WebUiNativeLifetime.Exit();

    /// <summary>Deletes all local browser profiles created by WebUI.</summary>
    public static void DeleteAllProfiles() => WebUiNative.DeleteAllProfiles();

    /// <summary>Releases WebUI's process-wide native resources.</summary>
    /// <remarks>
    /// <para>
    /// Call this only after all <see cref="WebUiWindow"/> instances are disposed. Like
    /// <see cref="Exit"/>, it first stops replies from asynchronous bindings that are still
    /// pending; a window whose disposal is still deferred by such a binding is then released
    /// without touching the cleaned native state. WebUI can be used again afterwards, and its
    /// process-wide configuration starts from its defaults.
    /// </para>
    /// <para>
    /// WebUI frees all native memory about two seconds after it exits, even while a binding or
    /// file-handler callback is still running on one of its threads. Before that, this method
    /// waits for callbacks running on other threads to return, for at most 75 seconds; a callback
    /// still running after that is no longer protected and must not use its event. WebUI itself
    /// releases each window about three seconds after exiting, even while its callbacks still run,
    /// so a callback that runs that long can find its event's arguments gone. Called from
    /// inside a callback, it only exits WebUI; call it again after <see cref="Wait"/> returns to
    /// release the native state.
    /// </para>
    /// </remarks>
    public static void Clean()
    {
        var insideCallback = WebUiWindow.IsDispatchingOnCurrentThread;
        WebUiNativeLifetime.Clean(insideCallback);
        if (!insideCallback)
        {
            // webui_clean() resets every native option, including asynchronous responses.
            Volatile.Write(ref _asynchronousResponsesEnabled, 0);
            Volatile.Write(ref _connectionTimeoutSeconds, 0);
            Volatile.Write(ref _browserFolder, null);
        }
    }

    /// <summary>Opens a URL in the user's default browser.</summary>
    public static unsafe void OpenUrl(string url)
    {
        var bytes = Utf8.Encode(url, nameof(url));
        fixed (byte* value = bytes)
        {
            WebUiNative.OpenUrl(value);
        }
    }

    /// <summary>Gets whether the host OS currently uses a high-contrast theme.</summary>
    public static bool IsHighContrast => WebUiNative.IsHighContrast() != 0;

    /// <summary>Gets an available local TCP port.</summary>
    public static nuint GetFreePort() => WebUiNative.GetFreePort();

    /// <summary>Gets whether a particular browser is installed.</summary>
    public static bool BrowserExists(WebUiBrowser browser)
        => WebUiNative.BrowserExist((nuint)browser) != 0;

    internal static byte NativeBoolean(bool value) => value ? (byte)1 : (byte)0;

    internal static bool AsynchronousResponsesEnabled
        => Volatile.Read(ref _asynchronousResponsesEnabled) != 0;

    /// <summary>Gets the folder set with <see cref="SetBrowserFolder"/>, if any.</summary>
    internal static string? BrowserFolder => Volatile.Read(ref _browserFolder);

    internal static TimeSpan ShowConnectionTimeout
    {
        get
        {
            var seconds = Volatile.Read(ref _connectionTimeoutSeconds);
            return TimeSpan.FromSeconds(seconds > 0 ? seconds : DefaultConnectionTimeoutSeconds);
        }
    }

    internal static void ResetConnectionTimeoutMirror() => Volatile.Write(ref _connectionTimeoutSeconds, 0);

    /// <summary>Gets the browser selected by <see cref="SetBrowserPath"/> or the environment variable.</summary>
    /// <returns><see langword="false"/> when the environment variable names a browser that cannot be used.</returns>
    internal static bool TryGetConfiguredBrowser(out ConfiguredBrowser? configured, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        lock (Gate)
        {
            configured = _browserPathSet ? _browserPath : null;
            error = null;
            if (_browserPathSet)
            {
                return true;
            }
        }

        var environment = Environment.GetEnvironmentVariable(BrowserPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(environment) || TryResolveBrowserPath(environment, out configured, out error))
        {
            return true;
        }

        error = $"{BrowserPathEnvironmentVariable} is set, but it cannot be used. {error}";
        return false;
    }

    /// <summary>Points WebUI's browser folder at a configured browser while showing a window.</summary>
    internal static T WithBrowserFolder<T>(ConfiguredBrowser configured, Func<T> show)
    {
        lock (BrowserFolderGate)
        {
            SetNativeBrowserFolder(configured.Folder);
            try
            {
                return show();
            }
            finally
            {
                // The folder is process-wide; restore the application's own choice.
                SetNativeBrowserFolder(Volatile.Read(ref _browserFolder) ?? string.Empty);
            }
        }
    }

    /// <summary>Reports a configuration problem that a non-throwing method cannot raise.</summary>
    internal static void ReportProblem(string message)
    {
        System.Diagnostics.Trace.TraceWarning("CsWebUi: " + message);
        try
        {
            Volatile.Read(ref _logger)?.Invoke(WebUiLogLevel.Error, message);
        }
        catch
        {
            // A logger failure must not turn a Try method into a throwing one.
        }
    }

    private static unsafe void SetNativeBrowserFolder(string path)
    {
        var bytes = Utf8.Encode(path, nameof(path));
        fixed (byte* value = bytes)
        {
            WebUiNative.SetBrowserFolder(value);
        }
    }

    internal static bool TryResolveBrowserPath(
        string path,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConfiguredBrowser? configured,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        configured = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "The browser path is empty.";
            return false;
        }

        // Path.GetFullPath throws for a null character instead of reporting it.
        if (path.Contains('\0'))
        {
            error = "The browser path contains the control character U+0000, which would end it early in WebUI's C string.";
            return false;
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            error = $"The browser '{fullPath}' does not exist.";
            return false;
        }

        if (FindShellUnsafeCharacter(fullPath) is { } unsafeCharacter)
        {
            error = $"The browser path '{fullPath}' contains {unsafeCharacter}, which is unsafe in the shell command WebUI runs to start the browser.";
            return false;
        }

        var folder = Path.GetDirectoryName(fullPath);
        if (WebUiBrowserLocator.Identify(fullPath) is not { } browser || string.IsNullOrEmpty(folder))
        {
            error = $"WebUI cannot launch '{fullPath}': it finds browsers only by these file names: " +
                $"{string.Join(", ", WebUiBrowserLocator.AllFolderNames())}. " +
                "Use one of them, for example through a symbolic link or a wrapper script.";
            return false;
        }

        configured = new ConfiguredBrowser(fullPath, folder, browser);
        error = null;
        return true;
    }

    /// <summary>Describes a character that breaks out of WebUI's double-quoted shell argument, if any.</summary>
    internal static string? FindShellUnsafeCharacter(string path)
    {
        foreach (var character in path)
        {
            if (char.IsControl(character))
            {
                return $"the control character U+{(int)character:X4}";
            }

            var unsafeCharacter = character == '"'
                || (OperatingSystem.IsWindows()
                    ? character == '%'
                    : character is '$' or '`' or '\\');
            if (unsafeCharacter)
            {
                return $"the character '{character}'";
            }
        }

        return null;
    }

    internal static WebUiException CreateNativeException()
    {
        var (errorNumber, message) = ReadNativeError();
        return new WebUiException(errorNumber, message);
    }

    internal static unsafe (nuint ErrorNumber, string? Message) ReadNativeError()
        => (WebUiNative.GetLastErrorNumber(), Utf8.Decode(WebUiNative.GetLastErrorMessage()));

    internal static void ReportUnhandledCallbackException(Exception exception, WebUiWindow window, WebUiEvent? webUiEvent)
    {
        try
        {
            UnhandledCallbackException?.Invoke(null, new WebUiUnhandledCallbackExceptionEventArgs(exception, window, webUiEvent));
        }
        catch
        {
            // Never allow a subscriber to propagate through an unmanaged callback.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void LoggerTrampoline(nuint level, byte* message, void* _)
    {
        try
        {
            Volatile.Read(ref _logger)?.Invoke((WebUiLogLevel)(uint)level, Utf8.DecodeRequired(message));
        }
        catch
        {
            // A native logger must not be allowed to observe a managed exception.
        }
    }
}

/// <summary>A browser selected by path, mapped onto WebUI's browser folder and browser kind.</summary>
internal sealed record ConfiguredBrowser(string Path, string Folder, WebUiBrowser Browser);
