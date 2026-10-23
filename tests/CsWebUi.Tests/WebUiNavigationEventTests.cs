using CsWebUi.Tests.Stress;

namespace CsWebUi.Tests;

/// <summary>
/// Navigation events carry their URL as argument 0. WebUI before <c>2b35db13f</c> did not count it
/// (webui-dev/webui#724), so <see cref="WebUiEvent.GetString"/> threw for a navigation event.
/// </summary>
public sealed class WebUiNavigationEventTests
{
    private const string Page = "<html><head><script src=\"/webui.js\"></script></head></html>";

    [NativeFact]
    public async Task NavigationEventExposesItsUrl()
    {
        const string target = "https://example.com/next?page=2";
        var observed = new TaskCompletionSource<(nuint Count, string Url)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = new WebUiWindow();
        window.Bind(string.Empty, e =>
        {
            if (e.EventType == WebUiEventType.Navigation)
            {
                try
                {
                    observed.TrySetResult((e.ArgumentCount, e.GetString()));
                }
                catch (Exception exception)
                {
                    observed.TrySetException(exception);
                }
            }
        });

        try
        {
            var url = new Uri(window.StartServer(Page));
            await using var client = await WebUiProtocolClient.ConnectAsync(url, CancellationToken.None);
            await client.AuthenticateAsync(CancellationToken.None);
            await client.SendNavigationAsync(target, CancellationToken.None);

            var (count, navigated) = await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal((nuint)1, count);
            Assert.Equal(target, navigated);
        }
        finally
        {
            WebUiApplication.Exit();
            window.Dispose();
            WebUiApplication.Clean();
        }
    }
}
