using System.Text;
using CsWebUi.Internal;
using CsWebUi.Tests.Stress;

namespace CsWebUi.Tests;

/// <summary>
/// <c>webui_script()</c> copies the result into the caller's buffer. Before WebUI 2456744e6 it left
/// that buffer registered after a timeout, so a late result was still copied into it; CS-WebUI
/// keeps such a buffer until Clean either way.
/// </summary>
public sealed class WebUiScriptBufferTests
{
    private const string Page = "<html><head><script src=\"/webui.js\"></script></head></html>";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [NativeFact]
    public async Task LateResultAfterATimeoutCorruptsNothing()
    {
        using var window = new WebUiWindow();
        try
        {
            await using var client = await ConnectAsync(window);
            var retained = WebUiNativeLifetime.RetainedBufferCount;

            // A buffer on the large object heap: if it were managed memory, a full collection would
            // free it and the next array of the same size would take its place.
            const int BufferSize = 128 * 1024;
            var timedOut = Task.Run(() => Record(() => window.ExecuteJavaScript("late()", TimeSpan.FromSeconds(1), BufferSize)));
            var (lateId, script) = await client.ReceiveScriptAsync(CancellationToken.None).WaitAsync(Wait);
            Assert.Equal("late()", script);
            Assert.IsType<WebUiException>(await timedOut.WaitAsync(Wait));
            Assert.Equal(retained + 1, WebUiNativeLifetime.RetainedBufferCount);

            // Let the managed heap reuse whatever the call allocated, then deliver the late result
            // into the buffer WebUI still has registered.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            var sentinels = Enumerable.Range(0, 8).Select(_ => new byte[BufferSize])
                .Concat(Enumerable.Range(0, 1024).Select(_ => new byte[64]))
                .ToArray();
            await client.SendScriptResultAsync(lateId, Encoding.ASCII.GetBytes(new string('X', 60)), CancellationToken.None);
            for (var i = 0; i < 4; i++)
            {
                // A round trip after the late result shows WebUI has processed it.
                Assert.Equal($"ok {i}", await RunAsync(window, client, $"ok {i}", 64));
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Assert.All(sentinels, sentinel => Assert.Equal(-1, sentinel.AsSpan().IndexOfAnyExcept((byte)0)));
            Assert.Equal(retained + 1, WebUiNativeLifetime.RetainedBufferCount);
        }
        finally
        {
            WebUiApplication.Exit();
            window.Dispose();
            WebUiApplication.Clean();
        }
    }

    [NativeFact]
    public async Task ResultsLongerThanTheBufferAreCutToIt()
    {
        using var window = new WebUiWindow();
        try
        {
            await using var client = await ConnectAsync(window);

            // Since 2456744e6 WebUI copies at most the buffer's size less one byte, and terminates it.
            Assert.Equal("0123456789abcde", await RunAsync(window, client, "0123456789abcdefXYZ", 16));

            // A character cut in half decodes to a replacement character, within the buffer.
            Assert.Equal("0123456789abcd�", await RunAsync(window, client, "0123456789abcdÜber", 16));

            // A result that fits ends at its terminator.
            Assert.Equal("short", await RunAsync(window, client, "short", 16));
        }
        finally
        {
            WebUiApplication.Exit();
            window.Dispose();
            WebUiApplication.Clean();
        }
    }

    [NativeFact]
    public async Task CleanFreesBuffersKeptForTimedOutCalls()
    {
        var window = new WebUiWindow();
        try
        {
            await using var client = await ConnectAsync(window);
            var retained = WebUiNativeLifetime.RetainedBufferCount;
            for (var i = 0; i < 2; i++)
            {
                var timedOut = Task.Run(() => Record(() => window.ExecuteJavaScript("never()", TimeSpan.FromSeconds(1), 32)));
                _ = await client.ReceiveScriptAsync(CancellationToken.None).WaitAsync(Wait);
                Assert.IsType<WebUiException>(await timedOut.WaitAsync(Wait));
            }

            Assert.Equal(retained + 2, WebUiNativeLifetime.RetainedBufferCount);
        }
        finally
        {
            WebUiApplication.Exit();
            window.Dispose();
            WebUiApplication.Clean();
        }

        Assert.Equal(0, WebUiNativeLifetime.RetainedBufferCount);
    }

    [NativeFact]
    public async Task ArgumentLengthsThatWrapAreRejected()
    {
        var forged = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = new WebUiWindow();
        window.Bind("forged", e => forged.TrySetResult(Record(() => e.GetString())));
        try
        {
            await using var client = await ConnectAsync(window);

            // Before 2456744e6, WebUI only checked that (100 + 1) + (n + 1) equals the 8 argument
            // bytes received; with n = 2^64 - 94 that sum wraps, and argument 0 claims 100 bytes it
            // does not have. Since then WebUI checks each length against the bytes left, drops the
            // call and replies with nothing; so does any WebUI where strtoul is 32-bit (Windows),
            // because the sum cannot wrap there. CS-WebUI's own check rejects the call for a WebUI
            // that still passes it to the callback.
            var payload = new byte[8];
            var id = await client.SendCallAsync("forged", "100;18446744073709551522", payload, CancellationToken.None);

            // WebUI replies after the callback has returned, or at once when it drops the call.
            Assert.Empty(await client.ReceiveReplyAsync(id, CancellationToken.None).WaitAsync(Wait));
            if (forged.Task.IsCompleted)
            {
                Assert.IsType<InvalidDataException>(await forged.Task);
            }
        }
        finally
        {
            WebUiApplication.Exit();
            window.Dispose();
            WebUiApplication.Clean();
        }
    }

    private static async Task<WebUiProtocolClient> ConnectAsync(WebUiWindow window)
    {
        var url = new Uri(window.StartServer(Page));
        var client = await WebUiProtocolClient.ConnectAsync(url, CancellationToken.None);
        try
        {
            await client.AuthenticateAsync(CancellationToken.None).WaitAsync(Wait);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs a script and answers it from the client with <paramref name="result"/>.</summary>
    private static async Task<string> RunAsync(WebUiWindow window, WebUiProtocolClient client, string result, int bufferSize)
    {
        var call = Task.Run(() => window.ExecuteJavaScript("answer()", TimeSpan.FromSeconds(30), bufferSize));
        var (id, _) = await client.ReceiveScriptAsync(CancellationToken.None).WaitAsync(Wait);
        await client.SendScriptResultAsync(id, Encoding.UTF8.GetBytes(result), CancellationToken.None);
        return await call.WaitAsync(Wait);
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
