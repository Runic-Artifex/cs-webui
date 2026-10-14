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

    [NativeFact]
    public async Task EventAccessAfterAReplyThrowsOnEveryThread()
    {
        var results = new TaskCompletionSource<(Exception? Synchronous, Exception? Later)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new WebUiWindow();
        try
        {
            window.BindAsync("answer", async (e, cancellationToken) =>
            {
                // Still on WebUI's callback thread: the reply is sent here.
                e.Respond("answered");
                var synchronous = Record(() => e.GetString());

                await Task.Yield();

                // On another thread; WebUI frees the event soon after the reply.
                var later = Record(() => e.RunJavaScript("void 0"));
                results.TrySetResult((synchronous, later));
                return "ignored";
            });

            var url = new Uri(window.StartServer("<html><head><script src=\"/webui.js\"></script></head></html>"));
            await using var client = await WebUiProtocolClient.ConnectAsync(url, CancellationToken.None);
            await client.CallAsync("answer", "argument", CancellationToken.None);

            var (synchronous, later) = await results.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains("already answered", Assert.IsType<InvalidOperationException>(synchronous).Message);
            Assert.Contains("already answered", Assert.IsType<InvalidOperationException>(later).Message);
        }
        finally
        {
            window.Dispose();
            WebUiApplication.Exit();
            WebUiApplication.Clean();
        }
    }

    [NativeFact]
    public async Task WindowIdsCanBeReusedAfterCleanWhileADisposalIsDeferred()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new WebUiWindow();
        var id = window.Id;
        try
        {
            window.BindAsync("pending", async (e, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return "late";
            });

            var url = new Uri(window.StartServer("<html><head><script src=\"/webui.js\"></script></head></html>"));
            await using (var client = await WebUiProtocolClient.ConnectAsync(url, CancellationToken.None))
            {
                await client.CallAsync("pending", "argument", CancellationToken.None);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

                WebUiApplication.Exit();
                window.Dispose(); // Deferred: the binding is still running.
                WebUiApplication.Clean();
            }

            using (var next = new WebUiWindow())
            {
                Assert.Equal(id, next.Id);
                release.TrySetResult(); // The old window's deferred disposal must leave the new one alone.
                await Task.Delay(200);
                Assert.StartsWith("http", next.StartServer("<html></html>"), StringComparison.Ordinal);
            }
        }
        finally
        {
            release.TrySetResult();
            window.Dispose();
            WebUiApplication.Exit();
            WebUiApplication.Clean();
        }
    }

    [NativeFact]
    public async Task CleanWaitsForASynchronousCallbackOnAnotherThread()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new WebUiWindow();
        try
        {
            window.Bind("slow", e =>
            {
                started.TrySetResult();

                // Longer than webui_exit() and webui_clean() wait for WebUI's threads before
                // webui_clean() frees all native memory.
                Thread.Sleep(TimeSpan.FromSeconds(5));
                try
                {
                    // WebUI has released the window by now (it waits 3 s for a window's tasks after
                    // exiting), so the arguments may be gone, but the native state must not be freed.
                    _ = e.ArgumentCount;
                }
                finally
                {
                    finished.TrySetResult();
                }

                return WebUiResult.None;
            });

            var url = new Uri(window.StartServer("<html><head><script src=\"/webui.js\"></script></head></html>"));
            await using var client = await WebUiProtocolClient.ConnectAsync(url, CancellationToken.None);
            await client.CallAsync("slow", "argument", CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            window.Dispose();
            var cleanStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            WebUiApplication.Clean();
            var cleanTook = System.Diagnostics.Stopwatch.GetElapsedTime(cleanStarted);

            Assert.True(finished.Task.IsCompleted, $"Clean returned after {cleanTook.TotalSeconds:F1} s, before the callback finished.");
        }
        finally
        {
            window.Dispose();
            WebUiApplication.Exit();
            WebUiApplication.Clean();
        }
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
