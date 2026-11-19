using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace CsWebUi;

/// <summary>The texts of the pages and messages CS-WebUI and WebUI show themselves.</summary>
/// <remarks>
/// <para>
/// Each text has a stable key, listed in <see cref="Keys"/>. A table may give any of them; the
/// others keep their English default (<see cref="English"/>). Give a window its texts with
/// <see cref="WebUiWindow.SetTexts"/>.
/// </para>
/// <list type="table">
/// <listheader><term>Text</term><description>Where it appears</description></listheader>
/// <item><term><see cref="NotFoundTitle"/>, <see cref="NotFoundMessage"/></term><description>The 404 page a file handler returns with <see cref="WebUiFileHandlerResult.NotFound"/>.</description></item>
/// <item><term><see cref="ServerErrorTitle"/>, <see cref="ServerErrorMessage"/></term><description>The 500 page CS-WebUI returns when a file handler throws or exceeds its response limit.</description></item>
/// <item><term><c>bridge.*</c></term><description>The banners WebUI's JavaScript bridge shows, which a page changes by running <see cref="BannerScript"/>.</description></item>
/// </list>
/// <para>
/// WebUI's own pages stay English: its 403 "Access Denied" page, which it sends before any handler
/// runs, and the 404 "Resource Not Available" page it sends when
/// <see cref="WebUiFileHandlerResult.NotHandled"/> leads to a path it cannot serve. WebUI has no
/// way to change them.
/// </para>
/// </remarks>
public sealed class WebUiTexts
{
    /// <summary>The title of the 404 page. English: "Resource Not Available".</summary>
    public const string NotFoundTitle = "not_found.title";

    /// <summary>The text of the 404 page. English: "The requested resource is not available.".</summary>
    public const string NotFoundMessage = "not_found.message";

    /// <summary>The title of the 500 page. English: "Internal Server Error".</summary>
    public const string ServerErrorTitle = "server_error.title";

    /// <summary>The text of the 500 page. English: "The application could not answer this request.".</summary>
    public const string ServerErrorMessage = "server_error.message";

    /// <summary>The label before each bridge banner's message. English: "Error:".</summary>
    public const string BridgeErrorPrefix = "bridge.error_prefix";

    /// <summary>The banner when the page cannot connect within five seconds. English: "Failed to connect to the backend application".</summary>
    public const string BridgeConnectionFailed = "bridge.connection_failed";

    /// <summary>The banner when the browser has no WebSocket. English: "WebSocket is not supported by your web browser".</summary>
    public const string BridgeWebSocketUnsupported = "bridge.websocket_unsupported";

    /// <summary>
    /// The banner when the connection ends and no event binding handles it. English: "Connection
    /// with the backend is lost".
    /// </summary>
    public const string BridgeConnectionLost = "bridge.connection_lost";

    // Element id and English messages of WebUI's bridge (bridge/webui.ts) at 24c7d196.
    internal const string BannerElementId = "webui-error-connection-lost";

    private static readonly FrozenDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [NotFoundTitle] = "Resource Not Available",
        [NotFoundMessage] = "The requested resource is not available.",
        [ServerErrorTitle] = "Internal Server Error",
        [ServerErrorMessage] = "The application could not answer this request.",
        [BridgeErrorPrefix] = "Error:",
        [BridgeConnectionFailed] = "Failed to connect to the backend application",
        [BridgeWebSocketUnsupported] = "WebSocket is not supported by your web browser",
        [BridgeConnectionLost] = "Connection with the backend is lost",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly string[] BannerKeys = [BridgeConnectionFailed, BridgeWebSocketUnsupported, BridgeConnectionLost];

    private readonly FrozenDictionary<string, string> _texts;
    private string? _bannerScript;

    /// <summary>Creates texts from a key → text table; keys it does not give stay English.</summary>
    /// <param name="texts">Texts by key; every key must be one of <see cref="Keys"/>.</param>
    /// <param name="language">
    /// The language of the texts as a BCP 47 tag, such as <c>de</c>, for the pages' <c>lang</c>
    /// attribute; <see langword="null"/> leaves it out.
    /// </param>
    /// <exception cref="ArgumentException">A key is unknown, or a text is <see langword="null"/>.</exception>
    public WebUiTexts(IReadOnlyDictionary<string, string> texts, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var merged = new Dictionary<string, string>(Defaults, StringComparer.Ordinal);
        foreach (var (key, text) in texts)
        {
            if (!Defaults.ContainsKey(key))
            {
                throw new ArgumentException($"'{key}' is not a CS-WebUI text key; see WebUiTexts.Keys.", nameof(texts));
            }

            merged[key] = text ?? throw new ArgumentException($"The text for '{key}' is null.", nameof(texts));
        }

        _texts = merged.ToFrozenDictionary(StringComparer.Ordinal);
        Language = string.IsNullOrWhiteSpace(language) ? null : language;
        NotFoundResponse = BuildPage(404, "Not Found", this[NotFoundTitle], this[NotFoundMessage]);
        ServerErrorResponse = BuildPage(500, "Internal Server Error", this[ServerErrorTitle], this[ServerErrorMessage]);
    }

    /// <summary>Gets the English texts, CS-WebUI's default.</summary>
    public static WebUiTexts English { get; } = new(new Dictionary<string, string>(), "en");

    /// <summary>Gets every text key, in a stable order.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        NotFoundTitle,
        NotFoundMessage,
        ServerErrorTitle,
        ServerErrorMessage,
        BridgeErrorPrefix,
        BridgeConnectionFailed,
        BridgeWebSocketUnsupported,
        BridgeConnectionLost,
    ];

    /// <summary>Gets the language of the texts, or <see langword="null"/>.</summary>
    public string? Language { get; }

    /// <summary>Gets the text for a key.</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="key"/> is not one of <see cref="Keys"/>.</exception>
    public string this[string key] => _texts[key];

    /// <summary>
    /// Gets a script that shows WebUI's bridge banners ("Error: Failed to connect to the backend
    /// application" and the others) with these texts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WebUI serves its bridge, <c>webui.js</c>, before any file handler runs, with the English
    /// messages built in, so CS-WebUI cannot change it. This script instead watches the page for
    /// the banner WebUI adds and rewrites it. Include it in the page, inline in a
    /// <c>&lt;script&gt;</c> element or served by the file handler; it needs no other script.
    /// </para>
    /// <para>
    /// This is fragile: it recognizes the banner by its element id,
    /// <c>webui-error-connection-lost</c>, and each message by its English text in WebUI's bridge at
    /// the pinned revision. If a later WebUI changes either, the banner stays English; it is never
    /// hidden or broken. The texts are inserted as text, never as HTML.
    /// </para>
    /// </remarks>
    public string BannerScript => _bannerScript ??= BuildBannerScript();

    internal byte[] NotFoundResponse { get; }

    internal byte[] ServerErrorResponse { get; }

    private string BuildBannerScript()
    {
        static string Js(string value) => "\"" + JavaScriptEncoder.Default.Encode(value) + "\"";

        var messages = new StringBuilder();
        foreach (var key in BannerKeys)
        {
            messages.Append(CultureInfo.InvariantCulture, $"[{Js(Defaults[key])},{Js(this[key])}],");
        }

        return string.Create(CultureInfo.InvariantCulture, $$"""
            (()=>{"use strict";
            const id={{Js(BannerElementId)}},prefix={{Js(this[BridgeErrorPrefix])}},lang={{Js(Language ?? "")}};
            const messages=[{{messages}}];
            const apply=()=>{
            const banner=document.getElementById(id);
            if(!banner||banner.dataset.cswebuiTexts==="1")return;
            const shown=banner.textContent||"";
            const match=messages.find(m=>shown.includes(m[0]));
            if(!match)return;
            const version=banner.querySelector("em");
            const label=document.createElement("strong");
            label.textContent=prefix;
            banner.replaceChildren(label," "+match[1]);
            if(version)banner.append(" - ",version);
            if(lang)banner.lang=lang;
            banner.dataset.cswebuiTexts="1";};
            new MutationObserver(apply).observe(document.documentElement,{childList:true,subtree:true});
            apply();})();
            """);
    }

    // The pages are UTF-8, so only the characters HTML gives a meaning are escaped.
    private static string Html(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);

    private byte[] BuildPage(int status, string reason, string title, string message)
    {
        var lang = Language is null ? string.Empty : $" lang=\"{Html(Language)}\"";
        var body = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $$$"""
            <!doctype html><html{{{lang}}}><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{{Html(title)}}}</title><style>body{font-family:system-ui,sans-serif;margin:2rem;color:#1f2328;background:#fff}@media (prefers-color-scheme:dark){body{color:#e6edf3;background:#0d1117}}</style></head><body><h1>{{{Html(title)}}}</h1><p>{{{Html(message)}}}</p></body></html>
            """));
        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n"));
        return [.. header, .. body];
    }
}
