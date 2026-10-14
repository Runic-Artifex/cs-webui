using System.Globalization;
using CsWebUi.Tests.Stress;

namespace CsWebUi.Tests;

/// <summary>Shutdown while asynchronous replies are pending (runic-sdk#239).</summary>
public sealed class WebUiShutdownTests
{
    private const string IterationsVariable = "CSWEBUI_SHUTDOWN_STRESS_ITERATIONS";

    [NativeFact]
    public async Task RepeatedShutdownsWithPendingAsyncRepliesDoNotCrash()
    {
        // Each iteration takes about a second: WebUI's exit and clean each sleep 500 ms.
        // Without the fix, nearly every iteration ends the test process with SIGSEGV in
        // webui_return_string. CsWebUi.ShutdownStress runs hundreds of separate processes.
        var iterations = int.TryParse(
            Environment.GetEnvironmentVariable(IterationsVariable),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var configured) && configured > 0
                ? configured
                : 8;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30 + (iterations * 10)));
        var random = new Random(239);

        for (var i = 0; i < iterations; i++)
        {
            await ShutdownStressScenario.RunOnceAsync(pendingCalls: 16, random, timeout.Token);
        }
    }

    [NativeFact]
    public async Task PendingEventIsReleasedWhenTheApplicationExits()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateAccess = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new WebUiWindow();
        try
        {
            window.BindAsync("pending", async (e, cancellationToken) =>
            {
                handlerStarted.TrySetResult();
                await release.Task.ConfigureAwait(false);
                try
                {
                    // WebUI stopped waiting for this reply when the application exited.
                    _ = e.GetString();
                    lateAccess.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    lateAccess.TrySetResult(exception);
                }

                return "discarded";
            });

            var url = new Uri(window.StartServer("<html><head><script src=\"/webui.js\"></script></head></html>"));
            await using (var client = await WebUiProtocolClient.ConnectAsync(url, CancellationToken.None))
            {
                await client.CallAsync("pending", "argument", CancellationToken.None);
                await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

                WebUiApplication.Exit();
                release.TrySetResult();

                var exception = await lateAccess.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var invalid = Assert.IsType<InvalidOperationException>(exception);
                Assert.Contains("released this event", invalid.Message);
            }
        }
        finally
        {
            release.TrySetResult();
            window.Dispose();
            WebUiApplication.Exit();
            WebUiApplication.Clean();
        }

        // WebUI is usable again after a clean.
        using var next = new WebUiWindow();
        Assert.StartsWith("http", next.StartServer("<html></html>"), StringComparison.Ordinal);
        WebUiApplication.Exit();
        next.Dispose();
        WebUiApplication.Clean();
    }
}
