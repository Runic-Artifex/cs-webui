using CsWebUi;

namespace CsWebUi.Tests;

public sealed class WebUiCloseHandlerTests
{
    [Fact]
    public void SupportIsReportedForWindowsAndLinuxOnly()
    {
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), WebUiWindow.IsCloseHandlerSupported);
    }

    [NativeFact]
    public void HandlerDecidesUntilTheApplicationClosesTheWindow()
    {
        using var window = new WebUiWindow();
        Assert.True(window.AllowsClose());

        var calls = 0;
        var answer = false;
        window.SetCloseHandler(() =>
        {
            calls++;
            return answer;
        });
        Assert.False(window.AllowsClose());
        answer = true;
        Assert.True(window.AllowsClose());
        Assert.Equal(2, calls);

        // GTK reports WebUI's own close as a delete-event; Close approves it without asking.
        answer = false;
        window.Close();
        Assert.True(window.AllowsClose());
        Assert.Equal(2, calls);

        // Showing the window again asks the handler again.
        window.StartServer("<html></html>");
        Assert.False(window.AllowsClose());
        Assert.Equal(3, calls);

        window.SetCloseHandler(null);
        Assert.True(window.AllowsClose());
        Assert.Equal(3, calls);
    }

    [NativeFact]
    public void AFailingHandlerIsReportedAndTheWindowCloses()
    {
        using var window = new WebUiWindow();
        var failure = new InvalidOperationException("close handler");
        Exception? reported = null;
        void OnException(object? sender, WebUiUnhandledCallbackExceptionEventArgs e) => reported = e.Exception;
        WebUiApplication.UnhandledCallbackException += OnException;
        try
        {
            window.SetCloseHandler(() => throw failure);
            Assert.True(window.AllowsClose());
            Assert.Same(failure, reported);
        }
        finally
        {
            WebUiApplication.UnhandledCallbackException -= OnException;
        }
    }

    [NativeFact]
    public void ADisposedWindowClosesAndRejectsAHandler()
    {
        var window = new WebUiWindow();
        window.SetCloseHandler(() => false);
        window.Dispose();

        Assert.True(window.AllowsClose());
        Assert.Throws<ObjectDisposedException>(() => window.SetCloseHandler(() => true));
    }
}
