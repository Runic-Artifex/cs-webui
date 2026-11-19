using CsWebUi;

namespace CsWebUi.Tests;

/// <summary>The window hooks on a real WebView window, which a test process opens once.</summary>
public sealed class WebUiWebViewWindowTests
{
    [WebViewFact]
    public async Task AWebViewWindowIsPlacedAndGuarded()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var session = new WebViewSession(window =>
        {
            window.SetSize(800, 600);
            window.SetCloseHandler(() =>
            {
                Interlocked.Increment(ref calls);
                asked.TrySetResult();
                return false;
            });
        });
        await session.ShownAsync();
        var window = session.Window;

        await PlacementIsReadAndRestoredAsync(window);

        // The user's close is refused and the window stays.
        NativeWindowClose.RequestUserClose(window);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.True(window.IsShown);
        Assert.False(session.WaitEnded.IsCompleted);

        // The application's close is not refused, and does not ask again (GTK would).
        // Wait ends only when no window of the process is left, which other tests may still have.
        window.Close();
        for (var attempt = 0; attempt < 150 && (window.IsShown || window.NativeHandle != 0); attempt++)
        {
            await Task.Delay(100);
        }

        Assert.False(window.IsShown);
        Assert.Equal(0, window.NativeHandle);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static async Task PlacementIsReadAndRestoredAsync(WebUiWindow window)
    {
        Assert.NotEqual(0, window.NativeHandle);
        Assert.True(window.TryGetPlacement(out var initial));
        Assert.False(initial.IsMaximized);
        Assert.True(initial.Width > 0 && initial.Height > 0);

        // A negative position, which WebUI's own SetPosition refuses.
        var target = new WebUiWindowPlacement(-40, 60, 640, 480, IsMaximized: false, initial.HasPosition);
        Assert.True(window.TrySetPlacement(target));
        var placed = await ReadUntilAsync(window, p => p == Expected(target));
        Assert.Equal(Expected(target), placed);

        if (!CanMaximize())
        {
            return;
        }

        // Maximized: the restored bounds stay readable, and restoring brings them back.
        Assert.True(window.TrySetPlacement(target with { IsMaximized = true }));
        var maximized = await ReadUntilAsync(window, p => p == Expected(target with { IsMaximized = true }));
        Assert.Equal(Expected(target with { IsMaximized = true }), maximized);

        Assert.True(window.TrySetPlacement(target));
        Assert.Equal(Expected(target), await ReadUntilAsync(window, p => p == Expected(target)));
    }

    // Wayland reports no position.
    private static WebUiWindowPlacement Expected(WebUiWindowPlacement target)
        => target.HasPosition ? target : target with { X = 0, Y = 0 };

    // Maximizing needs a window manager; xvfb-run starts none.
    private static bool CanMaximize()
        => OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CSWEBUI_WEBVIEW_TESTS_WM") == "1";

    // GTK applies a request when the window system confirms it.
    private static async Task<WebUiWindowPlacement> ReadUntilAsync(WebUiWindow window, Func<WebUiWindowPlacement, bool> done)
    {
        var placement = default(WebUiWindowPlacement);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            Assert.True(window.TryGetPlacement(out placement));
            if (done(placement))
            {
                break;
            }

            await Task.Delay(100);
        }

        return placement;
    }
}
