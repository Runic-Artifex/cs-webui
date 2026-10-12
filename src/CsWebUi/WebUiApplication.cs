using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CsWebUi.Internal;
using CsWebUi.Native;

namespace CsWebUi;

/// <summary>Provides process-wide WebUI configuration and lifetime operations.</summary>
public static class WebUiApplication
{
    private static readonly TimeSpan AsyncWaitPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly object Gate = new();
    private static Action<WebUiLogLevel, string>? _logger;
    private static int _asynchronousResponsesEnabled;

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
    public static void SetConnectionTimeout(nuint seconds)
        => WebUiNative.SetTimeout(seconds);

    /// <summary>Sets the folder in which WebUI searches for browser executables.</summary>
    public static unsafe void SetBrowserFolder(string path)
    {
        var bytes = Utf8.Encode(path, nameof(path));
        fixed (byte* value = bytes)
        {
            WebUiNative.SetBrowserFolder(value);
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
    public static void Exit() => WebUiNative.Exit();

    /// <summary>Deletes all local browser profiles created by WebUI.</summary>
    public static void DeleteAllProfiles() => WebUiNative.DeleteAllProfiles();

    /// <summary>Releases WebUI's process-wide native resources.</summary>
    /// <remarks>Call this only after all <see cref="WebUiWindow"/> instances are disposed.</remarks>
    public static void Clean() => WebUiNative.Clean();

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
