using CsWebUi;

var webRoot = Path.Combine(AppContext.BaseDirectory, "www");
if (!Directory.Exists(webRoot))
{
    throw new DirectoryNotFoundException($"The sample web assets were not copied to '{webRoot}'.");
}

WebUiApplication.SetConnectionTimeout(15);
WebUiApplication.SetLogger(static (level, message) => Console.WriteLine($"[WebUI:{level}] {message}"));
WebUiApplication.UnhandledCallbackException += static (_, args) =>
    Console.Error.WriteLine($"[Managed callback] {args.Exception}");

using (var window = new WebUiWindow())
{
    window.SetSize(1120, 760);
    window.SetMinimumSize(860, 600);
    window.SetResizable(true);
    window.SetHighContrast(WebUiApplication.IsHighContrast);
    window.Center();
    window.SetRootFolder(webRoot);

    window.Bind("getStatus", static webUiEvent =>
    {
        var window = webUiEvent.Window;
        return WebUiResult.FromString(
            $"Window {window.Id} · client {webUiEvent.ClientId} · port {window.Port} · " +
            $"high contrast: {WebUiApplication.IsHighContrast}");
    });

    window.Bind("calculate", static webUiEvent =>
        WebUiResult.FromDouble(webUiEvent.GetDouble() + webUiEvent.GetDouble(1)));

    window.BindAsync("delayedGreeting", static async (webUiEvent, cancellationToken) =>
    {
        var name = webUiEvent.GetString();
        await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken);
        return WebUiResult.FromString($"Hello, {name}. This response arrived asynchronously.");
    });

    window.Bind("sendRaw", static webUiEvent =>
    {
        var bytes = webUiEvent.GetBytes();
        webUiEvent.Window.SendRaw("receiveRaw", bytes);
        return WebUiResult.FromString($"Round-tripped {bytes.Length} byte(s) through WebUI's binary channel.");
    });

    window.Bind("setKiosk", static webUiEvent =>
    {
        var enabled = webUiEvent.GetBoolean();
        webUiEvent.Window.SetKiosk(enabled);
        return WebUiResult.FromString(enabled ? "Kiosk mode requested." : "Kiosk mode disabled.");
    });

    window.Bind("notifyFromBackend", static webUiEvent =>
    {
        webUiEvent.Window.RunJavaScript("showToast('This notification came from the .NET backend.');");
    });

    window.Bind("openWebUiSite", static _ => WebUiApplication.OpenUrl("https://webui.me"));
    window.Bind("exitApplication", static _ => WebUiApplication.Exit());

    window.Show("index.html");
    Console.WriteLine($"CS-WebUI High-Level Sample is running at {window.Url}");
    WebUiApplication.Wait();
}

WebUiApplication.Clean();
