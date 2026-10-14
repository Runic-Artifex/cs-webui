using System.Text;
using CsWebUi.Tests.Stress;

namespace CsWebUi.Tests;

/// <summary>
/// Strings with an embedded null character (webui-dev/webui#717): WebUI reads every string with
/// <c>strlen</c>, so it would see only the part before the null character.
/// </summary>
public sealed class WebUiNullCharacterTests
{
    private const string WithNull = "before\0after";
    private const string Page = "<html><head><script src=\"/webui.js\"></script></head></html>";

    private static readonly Dictionary<string, (string Parameter, Action<WebUiWindow> Call)> WindowApis = new()
    {
        ["Bind(action)"] = ("element", w => w.Bind(WithNull, _ => { })),
        ["Bind(result)"] = ("element", w => w.Bind(WithNull, _ => WebUiResult.None)),
        ["BindAsync"] = ("element", w => w.BindAsync(WithNull, (_, _) => ValueTask.FromResult(WebUiResult.None))),
        ["Show"] = ("content", w => w.Show(WithNull)),
        ["TryShow"] = ("content", w => w.TryShow(WithNull)),
        ["ShowInBrowser"] = ("content", w => w.ShowInBrowser(WithNull, WebUiBrowser.Chromium)),
        ["TryShowInBrowser"] = ("content", w => w.TryShowInBrowser(WithNull, WebUiBrowser.Chromium)),
        ["ShowWebView"] = ("content", w => w.ShowWebView(WithNull)),
        ["TryShowWebView"] = ("content", w => w.TryShowWebView(WithNull)),
        ["StartServer"] = ("content", w => w.StartServer(WithNull)),
        ["SetRootFolder"] = ("path", w => w.SetRootFolder(WithNull)),
        ["TrySetRootFolder"] = ("path", w => w.TrySetRootFolder(WithNull)),
        ["SetProfile(name)"] = ("name", w => w.SetProfile(WithNull, "profile")),
        ["SetProfile(path)"] = ("path", w => w.SetProfile("profile", WithNull)),
        ["SetProxy"] = ("proxyServer", w => w.SetProxy(WithNull)),
        ["SetCustomParameters"] = ("parameters", w => w.SetCustomParameters(WithNull)),
        ["SetIcon(icon)"] = ("icon", w => w.SetIcon(WithNull, "image/svg+xml")),
        ["SetIcon(mimeType)"] = ("mimeType", w => w.SetIcon("<svg></svg>", WithNull)),
        ["SetIconFile"] = ("path", w => w.SetIconFile(WithNull)),
        ["Navigate"] = ("url", w => w.Navigate(WithNull)),
        ["RunJavaScript"] = ("script", w => w.RunJavaScript(WithNull)),
        ["ExecuteJavaScript"] = ("script", w => w.ExecuteJavaScript(WithNull, TimeSpan.FromSeconds(1))),
        ["SendRaw"] = ("function", w => w.SendRaw(WithNull, [1, 2, 3])),
    };

    private static readonly Dictionary<string, (string Parameter, Action Call)> ApplicationApis = new()
    {
        ["SetBrowserFolder"] = ("path", () => WebUiApplication.SetBrowserFolder(WithNull)),
        ["SetBrowserPath"] = ("path", () => WebUiApplication.SetBrowserPath(WithNull)),
        ["SetDefaultRootFolder"] = ("path", () => WebUiApplication.SetDefaultRootFolder(WithNull)),
        ["OpenUrl"] = ("url", () => WebUiApplication.OpenUrl(WithNull)),
        ["WebUiResult.FromString"] = ("value", () => WebUiResult.FromString(WithNull)),
        ["WebUiResult implicit string"] = ("value", () => { WebUiResult result = WithNull; }),
    };

    public static TheoryData<string> WindowApiNames => new(WindowApis.Keys);

    public static TheoryData<string> ApplicationApiNames => new(ApplicationApis.Keys);

    [NativeTheory]
    [MemberData(nameof(WindowApiNames))]
    public void WindowApisRejectEmbeddedNullCharacters(string api)
    {
        var (parameter, call) = WindowApis[api];
        using var window = new WebUiWindow();

        var exception = Assert.Throws<ArgumentException>(() => call(window));
        Assert.Equal(parameter, exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(ApplicationApiNames))]
    public void ApplicationApisRejectEmbeddedNullCharacters(string api)
    {
        var (parameter, call) = ApplicationApis[api];

        var exception = Assert.Throws<ArgumentException>(call);
        Assert.Equal(parameter, exception.ParamName);
    }

    [Theory]
    [InlineData("/opt/browser\"; touch pwned; \"")]
    [InlineData("/opt/$HOME")]
    [InlineData("/opt/`id`")]
    [InlineData("/opt/new\nline")]
    public void BrowserFolderRejectsWhatTheShellWouldInterpret(string folder)
    {
        if (OperatingSystem.IsWindows() && folder.IndexOfAny(['$', '`']) >= 0)
        {
            // cmd.exe does not expand these inside double quotes; Windows rejects % instead.
            folder = folder.Replace('$', '%').Replace('`', '%');
        }

        var exception = Assert.Throws<ArgumentException>(() => WebUiApplication.SetBrowserFolder(folder));
        Assert.Equal("path", exception.ParamName);
    }

    [Fact]
    public void BrowserPathWithNullCharacterIsReportedNotThrown()
    {
        // The Try method used for CSWEBUI_BROWSER_PATH must report, not throw.
        Assert.False(WebUiApplication.TryResolveBrowserPath(WithNull, out var configured, out var error));
        Assert.Null(configured);
        Assert.Contains("U+0000", error, StringComparison.Ordinal);
    }

    [NativeFact]
    public async Task EventApisRejectEmbeddedNullCharactersAndArgumentsKeepThem()
    {
        var observed = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = new WebUiWindow();
        window.Bind("inspect", e =>
        {
            observed.TrySetResult(new Observation(
                e.GetString(),
                e.GetBytes(),
                [
                    ("script", Record(() => e.RunJavaScript(WithNull))),
                    ("url", Record(() => e.Navigate(WithNull))),
                    ("content", Record(() => e.Show(WithNull))),
                    ("function", Record(() => e.SendRaw(WithNull, [1]))),
                    ("value", Record(() => e.Respond(WithNull))),
                ]));
            return "ok";
        });

        try
        {
            var url = new Uri(window.StartServer(Page));
            await using var client = await WebUiProtocolClient.ConnectAsync(url, CancellationToken.None);
            await client.AuthenticateAsync(CancellationToken.None);
            var id = await client.CallAsync("inspect", WithNull, CancellationToken.None);

            var observation = await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Arguments are read with the size WebUI received, not up to the first null byte.
            Assert.Equal(WithNull, observation.Text);
            Assert.Equal(Encoding.UTF8.GetBytes(WithNull), observation.Bytes);
            foreach (var (parameter, exception) in observation.Failures)
            {
                Assert.Equal(parameter, Assert.IsType<ArgumentException>(exception).ParamName);
            }

            // The rejected Respond did not answer the call, so the binding's own result does.
            var reply = await client.ReceiveReplyAsync(id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("ok"u8.ToArray(), reply);
        }
        finally
        {
            WebUiApplication.Exit();
            window.Dispose();
            WebUiApplication.Clean();
        }
    }

    [NativeFact]
    public async Task RepliesReachJavaScriptWithExactlyTheirBytes()
    {
        // Reply strings are encoded into a fresh buffer and passed to webui_return_string(),
        // which copies them up to the terminator (PR #13). Stray bytes after the encoded text
        // would reach the client.
        // Large enough that GC.AllocateUninitializedArray really returns reused, unzeroed memory.
        var unicode = string.Concat(Enumerable.Repeat("Übersetzung 🧙 漢字 ", 512));
        var expected = Encoding.UTF8.GetBytes(unicode);
        var failures = new List<Exception>();
        using var window = new WebUiWindow();
        window.Bind("unicode", _ => unicode);
        window.Bind("empty", _ => string.Empty);
        window.BindAsync("asyncUnicode", async (_, _) =>
        {
            await Task.Yield();
            return unicode;
        });
        window.BindAsync("asyncEmpty", async (_, _) =>
        {
            await Task.Yield();
            return string.Empty;
        });
        window.BindAsync("asyncNull", async (_, _) =>
        {
            await Task.Yield();
            return WebUiResult.FromString(WithNull);
        });

        void OnUnhandled(object? sender, WebUiUnhandledCallbackExceptionEventArgs args)
        {
            lock (failures)
            {
                failures.Add(args.Exception);
            }
        }

        WebUiApplication.UnhandledCallbackException += OnUnhandled;
        try
        {
            var url = new Uri(window.StartServer(Page));
            await using var client = await WebUiProtocolClient.ConnectAsync(url, CancellationToken.None);
            await client.AuthenticateAsync(CancellationToken.None);

            for (var i = 0; i < 16; i++)
            {
                // Heap reuse between replies would expose a missing terminator.
                GC.AllocateUninitializedArray<byte>(expected.Length + 1).AsSpan().Fill(0x7B);
                if (i % 4 == 0)
                {
                    GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                }

                Assert.Equal(expected, await CallAsync(client, "unicode"));
                Assert.Equal(expected, await CallAsync(client, "asyncUnicode"));
            }

            Assert.Empty(await CallAsync(client, "empty"));
            Assert.Empty(await CallAsync(client, "asyncEmpty"));

            // A result with a null character is rejected; JavaScript gets the empty fallback reply.
            Assert.Empty(await CallAsync(client, "asyncNull"));
            lock (failures)
            {
                Assert.Equal("value", Assert.IsType<ArgumentException>(Assert.Single(failures)).ParamName);
            }
        }
        finally
        {
            WebUiApplication.UnhandledCallbackException -= OnUnhandled;
            WebUiApplication.Exit();
            window.Dispose();
            WebUiApplication.Clean();
        }
    }

    private static async Task<byte[]> CallAsync(WebUiProtocolClient client, string function)
    {
        var id = await client.CallAsync(function, "argument", CancellationToken.None);
        return await client.ReceiveReplyAsync(id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
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

    private sealed record Observation(string Text, byte[] Bytes, (string Parameter, Exception? Exception)[] Failures);
}
