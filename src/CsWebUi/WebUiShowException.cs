namespace CsWebUi;

/// <summary>Reports that WebUI could not show a window in a browser or WebView.</summary>
/// <remarks>
/// WebUI starts browsers detached and discards their output, so a browser's own exit code and
/// error output are not available. The message names the browser WebUI tried, where it was found,
/// and whether it could not be found, could not be started, or did not connect in time.
/// </remarks>
public sealed class WebUiShowException : WebUiException
{
    /// <summary>Initializes a show failure.</summary>
    public WebUiShowException(
        string message,
        WebUiShowFailure failure,
        WebUiBrowser browser,
        string? browserPath = null,
        TimeSpan? connectionTimeout = null)
        : base(0, message)
    {
        Failure = failure;
        Browser = browser;
        BrowserPath = browserPath;
        ConnectionTimeout = connectionTimeout;
    }

    /// <summary>Gets why the window could not be shown.</summary>
    public WebUiShowFailure Failure { get; }

    /// <summary>
    /// Gets the browser WebUI tried: a concrete browser, <see cref="WebUiBrowser.WebView"/>, or
    /// <see cref="WebUiBrowser.AnyBrowser"/> when no supported browser was found.
    /// </summary>
    public WebUiBrowser Browser { get; }

    /// <summary>Gets the browser executable or application WebUI most likely used, when it is known.</summary>
    public string? BrowserPath { get; }

    /// <summary>Gets how long WebUI waited for the browser to connect, for <see cref="WebUiShowFailure.NotConnected"/>.</summary>
    public TimeSpan? ConnectionTimeout { get; }
}

/// <summary>Identifies why WebUI could not show a window.</summary>
public enum WebUiShowFailure
{
    /// <summary>WebUI found no supported browser, and no WebView could be used instead.</summary>
    BrowserNotFound,

    /// <summary>WebUI found the browser but could not start it.</summary>
    BrowserNotStarted,

    /// <summary>WebUI started the browser, but the page did not connect back within the connection timeout.</summary>
    NotConnected,

    /// <summary>The embedded WebView could not be started.</summary>
    WebViewNotStarted,
}
