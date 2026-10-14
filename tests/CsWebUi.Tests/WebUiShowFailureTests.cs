using CsWebUi.Internal;

namespace CsWebUi.Tests;

/// <summary>A browser that cannot start ends with a message naming it and the reason (G-20, #19).</summary>
public sealed class WebUiShowFailureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void BrowserThatNeverConnectsIsNamedWithItsPathAndTimeout()
    {
        var exception = WebUiWindow.BuildShowException(
            1, WebUiBrowser.AnyBrowser, WebUiBrowser.Chromium, "/opt/chromium/bin/chromium", Timeout, Timeout, hasDisplay: true);

        Assert.Equal(WebUiShowFailure.NotConnected, exception.Failure);
        Assert.Equal(WebUiBrowser.Chromium, exception.Browser);
        Assert.Equal("/opt/chromium/bin/chromium", exception.BrowserPath);
        Assert.Equal(Timeout, exception.ConnectionTimeout);
        Assert.Contains("started Chromium (/opt/chromium/bin/chromium), but the page did not connect within 15 s", exception.Message);
        Assert.Contains("CSWEBUI_BROWSER_PATH", exception.Message);
        Assert.DoesNotContain("error 0", exception.Message);
        Assert.IsAssignableFrom<WebUiException>(exception);
    }

    [Fact]
    public void BrowserThatFailsQuicklyIsReportedAsNotStarted()
    {
        var exception = WebUiWindow.BuildShowException(
            2, WebUiBrowser.AnyBrowser, WebUiBrowser.Chrome, null, TimeSpan.FromMilliseconds(40), Timeout, hasDisplay: true);

        Assert.Equal(WebUiShowFailure.BrowserNotStarted, exception.Failure);
        Assert.Contains("could not start Google Chrome, the system's default browser, or the embedded WebView", exception.Message);
        Assert.Null(exception.ConnectionTimeout);
    }

    [Fact]
    public void MissingBrowserAndMissingDisplayAreBothReported()
    {
        var exception = WebUiWindow.BuildShowException(
            3, WebUiBrowser.AnyBrowser, WebUiBrowser.AnyBrowser, null, TimeSpan.FromMilliseconds(5), Timeout, hasDisplay: false);

        Assert.Equal(WebUiShowFailure.BrowserNotFound, exception.Failure);
        Assert.Contains("found no supported browser", exception.Message);
        Assert.Contains("the embedded WebView could not be started instead", exception.Message);
        Assert.Contains("DISPLAY and WAYLAND_DISPLAY are not set", exception.Message);
    }

    [Fact]
    public void WebViewFailureNamesTheWebView()
    {
        var exception = WebUiWindow.BuildShowException(
            4, WebUiBrowser.WebView, WebUiBrowser.WebView, null, TimeSpan.Zero, Timeout, hasDisplay: true);

        Assert.Equal(WebUiShowFailure.WebViewNotStarted, exception.Failure);
        Assert.Contains("the embedded WebView could not be started", exception.Message);
    }

    [Fact]
    public void BrowserPathMapsOntoWebUisFolderAndBrowserKind()
    {
        var folder = Directory.CreateTempSubdirectory("cswebui-browser-");
        try
        {
            var name = WebUiBrowserLocator.GetFolderNames(WebUiBrowser.Firefox)[0];
            var path = Path.Combine(folder.FullName, name);
            if (OperatingSystem.IsMacOS())
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllText(path, string.Empty);
            }

            Assert.True(WebUiApplication.TryResolveBrowserPath(path, out var configured, out _));
            Assert.Equal(WebUiBrowser.Firefox, configured.Browser);
            Assert.Equal(folder.FullName, configured.Folder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnknownBrowserFileNamesAreRejectedWithTheAcceptedNames()
    {
        var file = Path.GetTempFileName();
        try
        {
            Assert.False(WebUiApplication.TryResolveBrowserPath(file, out _, out var error));
            Assert.Contains("finds browsers only by these file names", error);
            Assert.Contains(WebUiBrowserLocator.GetFolderNames(WebUiBrowser.Chromium)[0], error);

            var argument = Assert.Throws<ArgumentException>(() => WebUiApplication.SetBrowserPath(file));
            Assert.Equal("path", argument.ParamName);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void MissingBrowserPathIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "chromium");

        Assert.False(WebUiApplication.TryResolveBrowserPath(path, out _, out var error));
        Assert.Contains("does not exist", error);
    }

    [NativeFact]
    public void ShowNamesAConfiguredBrowserThatNeverConnects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // The fake browser below is a shell script; WebUI launches macOS browsers through Launch Services.
        }

        var folder = Directory.CreateTempSubdirectory("cswebui-browser-");
        var browser = Path.Combine(folder.FullName, "chromium");
        File.WriteAllText(browser, "#!/bin/sh\necho 'cannot render' >&2\nexit 1\n");
        File.SetUnixFileMode(browser, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            WebUiApplication.SetBrowserPath(browser);
            WebUiApplication.SetConnectionTimeout(2);
            using var window = new WebUiWindow();
            window.SetProfile("cswebui-test", Path.Combine(folder.FullName, "profile"));

            var exception = Assert.Throws<WebUiShowException>(() => window.Show("<html><head><script src=\"/webui.js\"></script></head></html>"));

            Assert.Equal(WebUiShowFailure.NotConnected, exception.Failure);
            Assert.Equal(WebUiBrowser.Chromium, exception.Browser);
            Assert.Equal(browser, exception.BrowserPath);
            Assert.Contains($"started Chromium ({browser}), but the page did not connect within 2 s", exception.Message);
        }
        finally
        {
            WebUiApplication.SetBrowserPath(null);
            WebUiApplication.SetConnectionTimeout(0);
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0.95, WebUiShowFailure.NotConnected)]
    [InlineData(0.1, WebUiShowFailure.BrowserNotStarted)]
    public void OneSecondTimeoutsAreClassifiedByElapsedTime(double elapsedSeconds, WebUiShowFailure expected)
    {
        var exception = WebUiWindow.BuildShowException(
            5, WebUiBrowser.Chromium, WebUiBrowser.Chromium, null, TimeSpan.FromSeconds(elapsedSeconds), TimeSpan.FromSeconds(1), hasDisplay: true);

        Assert.Equal(expected, exception.Failure);
    }

    [Theory]
    [InlineData("/opt/chrome\"bin/chromium")]
    [InlineData("/opt/chrome\nbin/chromium")]
    public void ShellBreakingCharactersAreFoundOnEveryPlatform(string path)
        => Assert.NotNull(WebUiApplication.FindShellUnsafeCharacter(path));

    [Fact]
    public void ShellUnsafeBrowserPathsAreRejected()
    {
        var unsafeName = OperatingSystem.IsWindows() ? "cswebui-%PATH%-" : "cswebui-$(id)-";
        var folder = Directory.CreateTempSubdirectory(unsafeName);
        try
        {
            var name = WebUiBrowserLocator.GetFolderNames(WebUiBrowser.Firefox)[0];
            var path = Path.Combine(folder.FullName, name);
            if (OperatingSystem.IsMacOS())
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllText(path, string.Empty);
            }

            Assert.False(WebUiApplication.TryResolveBrowserPath(path, out _, out var error));
            Assert.Contains("unsafe in the shell command", error);
            var argument = Assert.Throws<ArgumentException>(() => WebUiApplication.SetBrowserPath(path));
            Assert.Contains("unsafe", argument.Message);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [NativeFact]
    public void TryShowReportsAnUnusableBrowserVariableInsteadOfThrowing()
    {
        var previous = Environment.GetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable);
        var reported = new List<string>();
        Environment.SetEnvironmentVariable(
            WebUiApplication.BrowserPathEnvironmentVariable,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "chromium"));
        WebUiApplication.SetBrowserPath(null);
        WebUiApplication.SetLogger((_, message) => reported.Add(message));
        try
        {
            using var window = new WebUiWindow();

            Assert.False(window.TryShow("<html></html>"));
            Assert.False(window.TryShowInBrowser("<html></html>", WebUiBrowser.Chromium));
            Assert.Contains(reported, message => message.Contains("CSWEBUI_BROWSER_PATH is set, but it cannot be used", StringComparison.Ordinal));
            var exception = Assert.Throws<WebUiException>(() => window.Show("<html></html>"));
            Assert.Contains("does not exist", exception.Message);
        }
        finally
        {
            WebUiApplication.SetLogger(null);
            Environment.SetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable, previous);
        }
    }
}
