namespace CsWebUi.Tests.Stress;

/// <summary>
/// One host lifetime that shuts down while asynchronous bridge replies are pending
/// (runic-sdk#239): a served window with an async binding, a client with several calls
/// in flight, then <see cref="WebUiApplication.Exit"/>, disposal and
/// <see cref="WebUiApplication.Clean"/> while the replies complete.
/// </summary>
/// <remarks>
/// WebUI processes every WebSocket message on its own native thread, and the
/// <c>webui_event_t</c> handed to the callback lives on that thread's stack. With
/// asynchronous responses enabled, that thread waits for the reply, but stops waiting
/// as soon as the application exits, frees the event and ends. A reply written after
/// that point dereferences a dead stack frame. Many pending calls make the freed
/// stacks exceed glibc's thread-stack cache, so they are unmapped and a late reply
/// faults rather than silently reading stale memory.
/// </remarks>
internal static class ShutdownStressScenario
{
    private const string PendingFunction = "pending";
    private static int _nextWindowId;

    /// <param name="pendingCalls">How many calls are still waiting for their reply at shutdown.</param>
    /// <param name="random">Chooses when the replies are released.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="sendTogether">
    /// Sends every call at once instead of one at a time (webui-dev/webui#721). WebUI before
    /// 9f36ede5d assigned event slots without holding its lock, so calls arriving together could
    /// share one slot.
    /// </param>
    internal static async Task RunOnceAsync(int pendingCalls, Random random, CancellationToken cancellationToken, bool sendTogether = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pendingCalls, 1);

        using var admitted = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A window whose disposal is deferred by a late reply stays registered under its
        // id; rotating ids keeps the next iteration from colliding with it.
        var windowId = (nuint)(1 + (Interlocked.Increment(ref _nextWindowId) % 1000));
        var window = new WebUiWindow(windowId);
        var cleaned = false;
        try
        {
            window.BindAsync(PendingFunction, async (e, _) =>
            {
                // Read the event while the native callback is still on the stack.
                var argument = e.GetString();
                admitted.Release();

                await release.Task.ConfigureAwait(false);

                // A non-empty string goes through webui_return_string().
                return WebUiResult.FromString("late reply " + argument);
            });

            var url = new Uri(window.StartServer("<html><head><script src=\"/webui.js\"></script></head><body></body></html>"));
            await using var client = await WebUiProtocolClient.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < pendingCalls; i++)
            {
                await client.CallAsync(PendingFunction, i.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken)
                    .ConfigureAwait(false);

                // By default, send the next call only once this one reached its binding, as
                // webui.js-based clients such as Runic's do: WebUI before 9f36ede5d assigned event
                // slots without holding its lock (_webui_new_event_inf), so calls that arrive
                // together could share one slot.
                if (!sendTogether && !await admitted.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
                {
                    throw new TimeoutException($"Call {i} did not reach its binding.");
                }
            }

            for (var i = 0; sendTogether && i < pendingCalls; i++)
            {
                if (!await admitted.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
                {
                    throw new TimeoutException($"Only {i} of {pendingCalls} calls sent together reached their binding.");
                }
            }

            // Release the replies at a random point: some complete while WebUI is exiting,
            // others after it stopped waiting for them, or while it cleans up.
            var releaseDelay = TimeSpan.FromMilliseconds(random.Next(0, 1200));
            var releaser = Task.Run(
                async () =>
                {
                    await Task.Delay(releaseDelay, cancellationToken).ConfigureAwait(false);
                    release.TrySetResult();
                },
                cancellationToken);

            WebUiApplication.Exit();
            window.Dispose();
            WebUiApplication.Clean();
            cleaned = true;
            await releaser.ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
            window.Dispose();
            if (!cleaned)
            {
                WebUiApplication.Exit();
                WebUiApplication.Clean();
            }
        }
    }
}
