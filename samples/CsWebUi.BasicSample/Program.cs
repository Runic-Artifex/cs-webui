using CsWebUi;

using var window = new WebUiWindow();

window.Bind("multiply", static webUiEvent =>
    WebUiResult.FromInt64(webUiEvent.GetInt64() * webUiEvent.GetInt64(1)));

window.BindAsync("greet", static (webUiEvent, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.FromResult<WebUiResult>($"Hello, {webUiEvent.GetString()}!");
});

window.Show("""
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <script src="webui.js"></script>
      <title>CS-WebUI</title>
    </head>
    <body>
      <h1>CS-WebUI is running</h1>
      <button onclick="multiply(6, 7).then(value => alert(`6 × 7 = ${value}`))">Multiply</button>
      <button onclick="greet('WebUI').then(alert)">Greet</button>
    </body>
    </html>
    """);

WebUiApplication.Wait();
