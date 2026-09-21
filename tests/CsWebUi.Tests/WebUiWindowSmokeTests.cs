using CsWebUi;
using CsWebUi.Native;

namespace CsWebUi.Tests;

public sealed class WebUiWindowSmokeTests
{
    [Fact]
    public async Task WaitAsyncHonorsPreCanceledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WebUiApplication.WaitAsync(cancellation.Token));
    }

    [NativeFact]
    public void HighLevelWindowOwnsAndDestroysANativeWindowWhenConfigured()
    {
        using var window = new WebUiWindow();

        window.SetEventBlocking(true);
        window.SetEventBlocking(false);
        window.SetFrameless(true);
        window.SetFrameless(false);
        window.SetTransparent(true);
        window.SetTransparent(false);
        window.SetCustomParameters("--disable-gpu");
        window.SetIcon("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", "image/svg+xml");

        Assert.NotEqual((nuint)0, window.Id);
        Assert.False(window.IsShown);
        Assert.InRange(window.BestBrowser, WebUiBrowser.NoBrowser, WebUiBrowser.WebView);
    }
}
