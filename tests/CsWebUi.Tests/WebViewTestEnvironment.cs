using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CsWebUi;
using CsWebUi.Native;

namespace CsWebUi.Tests;

/// <summary>
/// Runs only when <c>CSWEBUI_WEBVIEW_TESTS=1</c> and a native library are set: these tests open a
/// real embedded WebView window (WebView2 on Windows, WebKitGTK on Linux, which needs a display such
/// as <c>xvfb-run</c>). CI does not set the variable.
/// </summary>
internal sealed class WebViewFactAttribute : FactAttribute
{
    public WebViewFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY")))
        {
            Skip = NativeTestEnvironment.MissingLibraryReason;
        }
        else if (Environment.GetEnvironmentVariable("CSWEBUI_WEBVIEW_TESTS") != "1")
        {
            Skip = "Set CSWEBUI_WEBVIEW_TESTS=1 to open real WebView windows.";
        }
        else if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Skip = "WebView window tests run on Windows and Linux.";
        }
    }
}

/// <summary>Shows one WebView window on a thread of its own, which then runs WebUI's wait loop.</summary>
/// <remarks>
/// A test process opens one session only. WebKitGTK stays bound to the thread that first created
/// a WebView, and with WebUI at 24c7d196 a second <c>webui_wait()</c> in a Linux WebView process
/// did not return after its window closed, so tests that need a real window share one.
/// </remarks>
internal sealed class WebViewSession : IDisposable
{
    internal const string Page = "<!doctype html><html><head><script src=\"/webui.js\"></script></head><body>CS-WebUI</body></html>";

    private static int s_opened;

    private readonly TaskCompletionSource _shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _waitEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal WebViewSession(Action<WebUiWindow>? configure = null)
    {
        Assert.True(Interlocked.Exchange(ref s_opened, 1) == 0, "A test process opens one WebView session only.");
        Window = new WebUiWindow();
        configure?.Invoke(Window);
        new Thread(() =>
        {
            try
            {
                Window.ShowWebView(Page);
                _shown.TrySetResult();
                WebUiApplication.Wait();
            }
            catch (Exception exception)
            {
                _shown.TrySetException(exception);
            }
            finally
            {
                _waitEnded.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "CS-WebUI WebView test",
        }.Start();
    }

    internal WebUiWindow Window { get; }

    internal Task WaitEnded => _waitEnded.Task;

    internal Task ShownAsync() => _shown.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        if (!_waitEnded.Task.IsCompleted && _shown.Task.IsCompletedSuccessfully)
        {
            Window.Close();
        }

        // Wait goes on while other tests' windows exist; then end it, and clean so they can go on.
        if (!_waitEnded.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            WebUiApplication.Exit();
            _waitEnded.Task.Wait(TimeSpan.FromSeconds(15));
            Window.Dispose();
            WebUiApplication.Clean();
            return;
        }

        Window.Dispose();
    }
}

/// <summary>Closes a WebView window the way its close button does.</summary>
internal static unsafe partial class NativeWindowClose
{
    private const uint WmClose = 0x0010;

    internal static void RequestUserClose(WebUiWindow window)
    {
        var handle = WebUiNative.GetHwnd(window.Id);
        Assert.True(handle is not null, "WebUI reported no native window handle.");
        if (OperatingSystem.IsWindows())
        {
            // The close button and Alt+F4 reach the window procedure as WM_CLOSE.
            Assert.True(PostMessageW((nint)handle, WmClose, 0, 0) != 0);
            return;
        }

        // gtk_window_close sends the window manager's delete-event; run it on GTK's main loop.
        var gtk = NativeLibrary.Load("libgtk-3.so.0");
        var glib = NativeLibrary.Load("libglib-2.0.so.0");
        s_gtkWindowClose = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(gtk, "gtk_window_close");
        var idleAdd = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<void*, int>, void*, uint>)
            NativeLibrary.GetExport(glib, "g_idle_add");
        idleAdd(&CloseOnMainLoop, handle);
    }

    private static delegate* unmanaged[Cdecl]<void*, void> s_gtkWindowClose;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CloseOnMainLoop(void* gtkWindow)
    {
        s_gtkWindowClose(gtkWindow);
        return 0;
    }

    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
