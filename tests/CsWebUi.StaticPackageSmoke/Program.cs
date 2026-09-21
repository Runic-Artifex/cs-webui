using CsWebUi;

var port = WebUiApplication.GetFreePort();
var webViewAvailable = WebUiApplication.BrowserExists(WebUiBrowser.WebView);
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

using (var window = new WebUiWindow())
{
    window.SetFileHandler(_ => WebUiFileHandlerResult.FromResponse(
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 22\r\nConnection: close\r\n\r\nCS-WebUI package smoke"u8.ToArray()));
    var serverUrl = window.StartServer("index.html");
    var content = await client.GetStringAsync(serverUrl);
    if (!string.Equals(content, "CS-WebUI package smoke", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("The server-only package smoke returned unexpected content.");
        return 1;
    }
}

Console.WriteLine($"WebUI allocated port {port}, served content, and disposed cleanly. WebView available: {webViewAvailable}.");
return port == 0 ? 1 : 0;
