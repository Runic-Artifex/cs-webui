using System.Net;
using System.Text;
using CsWebUi;

namespace CsWebUi.Tests;

public sealed class WebUiTextsTests
{
    internal static readonly Dictionary<string, string> German = new()
    {
        [WebUiTexts.NotFoundTitle] = "Ressource nicht verfügbar",
        [WebUiTexts.NotFoundMessage] = "Die angeforderte Ressource ist <nicht> verfügbar.",
        [WebUiTexts.ServerErrorTitle] = "Interner Serverfehler",
        [WebUiTexts.BridgeErrorPrefix] = "Fehler:",
        [WebUiTexts.BridgeConnectionFailed] = "Verbindung zur Anwendung fehlgeschlagen",
    };

    [Fact]
    public void KeysAreStableAndEveryKeyHasAnEnglishText()
    {
        Assert.Equal(
            [
                "not_found.title", "not_found.message", "server_error.title", "server_error.message",
                "bridge.error_prefix", "bridge.connection_failed", "bridge.websocket_unsupported", "bridge.connection_lost",
            ],
            WebUiTexts.Keys);
        Assert.All(WebUiTexts.Keys, key => Assert.False(string.IsNullOrEmpty(WebUiTexts.English[key])));
        Assert.Equal("en", WebUiTexts.English.Language);
        Assert.Equal("Resource Not Available", WebUiTexts.English[WebUiTexts.NotFoundTitle]);
    }

    [Fact]
    public void ATableOverridesSomeKeysAndKeepsTheOthersEnglish()
    {
        var texts = new WebUiTexts(German, "de");

        Assert.Equal("Fehler:", texts[WebUiTexts.BridgeErrorPrefix]);
        Assert.Equal("The application could not answer this request.", texts[WebUiTexts.ServerErrorMessage]);
        Assert.Equal("de", texts.Language);
        Assert.Throws<KeyNotFoundException>(() => texts["unknown"]);
    }

    [Fact]
    public void UnknownKeysAndNullTextsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new WebUiTexts(new Dictionary<string, string> { ["not_found"] = "x" }));
        Assert.Throws<ArgumentException>(() => new WebUiTexts(new Dictionary<string, string> { [WebUiTexts.NotFoundTitle] = null! }));
        Assert.Throws<ArgumentNullException>(() => new WebUiTexts(null!));
    }

    [Fact]
    public void PagesAreCompleteResponsesWithEncodedTexts()
    {
        var texts = new WebUiTexts(German, "de");
        var (status, headers, body) = Split(texts.NotFoundResponse);

        Assert.Equal("HTTP/1.1 404 Not Found", status);
        Assert.Contains("Content-Type: text/html; charset=utf-8", headers);
        Assert.Contains($"Content-Length: {Encoding.UTF8.GetByteCount(body)}", headers);
        Assert.Contains("<html lang=\"de\">", body);
        Assert.Contains("<title>Ressource nicht verfügbar</title>", body);
        Assert.Contains("Die angeforderte Ressource ist &lt;nicht&gt; verfügbar.", body);

        (status, _, body) = Split(texts.ServerErrorResponse);
        Assert.Equal("HTTP/1.1 500 Internal Server Error", status);
        Assert.Contains("<h1>Interner Serverfehler</h1>", body);
        Assert.DoesNotContain(" lang=", Split(new WebUiTexts(German).NotFoundResponse).Body);
    }

    [Fact]
    public void TheBannerScriptCarriesTheTextsAsJavaScriptStrings()
    {
        var script = new WebUiTexts(German, "de").BannerScript;

        Assert.Contains(WebUiTexts.BannerElementId, script);
        Assert.Contains("Failed to connect to the backend application", script);
        Assert.Contains("Verbindung zur Anwendung fehlgeschlagen", script);
        Assert.Contains("Connection with the backend is lost", script);
        Assert.DoesNotContain("<", script);
        Assert.DoesNotContain("innerHTML", script);
    }

    [Fact]
    public void NotFoundIsAHandledResultWithoutBytes()
    {
        Assert.True(WebUiFileHandlerResult.NotFound.IsHandled);
        Assert.True(WebUiFileHandlerResult.NotFound.IsNotFound);
        Assert.True(WebUiFileHandlerResult.NotFound.Response.IsEmpty);
        Assert.False(WebUiFileHandlerResult.NotHandled.IsNotFound);
        Assert.False(WebUiFileHandlerResult.FromResponse("HTTP/1.1 204 No Content\r\n\r\n"u8.ToArray()).IsNotFound);
    }

    [NativeFact]
    public async Task TheWindowAnswersNotFoundAndFailuresWithItsTexts()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var window = new WebUiWindow();
        window.SetFileHandler(path => path == "/fail" ? throw new InvalidOperationException("contained") : WebUiFileHandlerResult.NotFound);
        var serverUrl = window.StartServer("index.html");

        using (var english = await client.GetAsync(serverUrl))
        {
            Assert.Equal(HttpStatusCode.NotFound, english.StatusCode);
            Assert.Contains("<h1>Resource Not Available</h1>", await english.Content.ReadAsStringAsync());
        }

        window.SetTexts(new WebUiTexts(German, "de"));
        using (var german = await client.GetAsync(serverUrl))
        {
            Assert.Equal(HttpStatusCode.NotFound, german.StatusCode);
            Assert.Contains("<h1>Ressource nicht verfügbar</h1>", await german.Content.ReadAsStringAsync());
        }

        using (var failure = await client.GetAsync(new Uri(new Uri(serverUrl), "/fail")))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
            Assert.Contains("<h1>Interner Serverfehler</h1>", await failure.Content.ReadAsStringAsync());
        }

        window.SetTexts(null);
        using (var plain = await client.GetAsync(new Uri(new Uri(serverUrl), "/fail")))
        {
            Assert.Equal("Internal Server Error", await plain.Content.ReadAsStringAsync());
        }
    }

    // A canary for BannerScript: WebUI's bridge must still build its banner with this id and these
    // English messages. When WebUI changes them, update WebUiTexts.
    [NativeFact]
    public async Task TheBridgeStillShowsTheBannersTheScriptRecognizes()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var window = new WebUiWindow();
        var serverUrl = window.StartServer("<html><head><script src=\"/webui.js\"></script></head></html>");

        var bridge = await client.GetStringAsync(new Uri(new Uri(serverUrl), "/webui.js"));

        Assert.Contains(WebUiTexts.BannerElementId, bridge);
        Assert.Contains("<strong>Error:</strong>", bridge);
        Assert.All(
            [WebUiTexts.BridgeConnectionFailed, WebUiTexts.BridgeWebSocketUnsupported, WebUiTexts.BridgeConnectionLost],
            key => Assert.Contains(WebUiTexts.English[key], bridge));
    }

    private static (string Status, string Headers, string Body) Split(byte[] response)
    {
        var text = Encoding.UTF8.GetString(response);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = text[..end];
        return (head[..head.IndexOf("\r\n", StringComparison.Ordinal)], head, text[(end + 4)..]);
    }
}
