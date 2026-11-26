using System.Collections.Concurrent;
using System.Net;
using System.Text;
using CsWebUi;

namespace CsWebUi.Tests;

/// <summary>
/// WebUI redirects <c>/</c> and folder URLs to their index file. Before webui-dev/webui#729 the
/// redirect dropped the query string, so a page opened at <c>/?mode=x</c> lost <c>?mode=x</c>
/// (cs-webui#33).
/// </summary>
public sealed class WebUiIndexRedirectTests
{
    private const string Query = "?a=1&b=2";

    [NativeFact]
    public async Task FileHandlerIndexRedirectKeepsTheQuery()
    {
        var requested = new ConcurrentQueue<string>();
        using var window = new WebUiWindow();
        window.SetFileHandler(path =>
        {
            requested.Enqueue(path);
            return path is "/index.html" or "/docs/index.html"
                ? TextResponse($"page {path}")
                : WebUiFileHandlerResult.NotHandled;
        });
        var server = new Uri(window.StartServer("index.html"));
        using var client = CreateClient();

        // "/" names the window's index file.
        var (location, body) = await FollowRedirectAsync(client, new Uri(server, "/" + Query));
        Assert.EndsWith("/index.html" + Query, location.OriginalString, StringComparison.Ordinal);
        Assert.Equal("page /index.html", body);

        // A folder the handler serves only through its index file.
        (location, body) = await FollowRedirectAsync(client, new Uri(server, "/docs/" + Query));
        Assert.EndsWith("/docs/index.html" + Query, location.OriginalString, StringComparison.Ordinal);
        Assert.Equal("page /docs/index.html", body);

        // The handler gets the path without the query.
        Assert.DoesNotContain(requested, path => path.Contains('?', StringComparison.Ordinal));
    }

    [NativeFact]
    public async Task RootFolderIndexRedirectKeepsTheQuery()
    {
        var root = Directory.CreateTempSubdirectory("cswebui-redirect-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "index.html"), "<html><body>root</body></html>");
            var docs = Directory.CreateDirectory(Path.Combine(root.FullName, "docs"));
            File.WriteAllText(Path.Combine(docs.FullName, "index.html"), "<html><body>docs</body></html>");

            using var window = new WebUiWindow();
            window.SetRootFolder(root.FullName);
            var server = new Uri(window.StartServer("index.html"));
            using var client = CreateClient();

            var (location, body) = await FollowRedirectAsync(client, new Uri(server, "/" + Query));
            Assert.EndsWith("index.html" + Query, location.OriginalString, StringComparison.Ordinal);
            Assert.Contains("root", body, StringComparison.Ordinal);

            (location, body) = await FollowRedirectAsync(client, new Uri(server, "/docs/" + Query));
            Assert.EndsWith("/docs/index.html" + Query, location.OriginalString, StringComparison.Ordinal);
            Assert.Contains("docs", body, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // Requests the URL without following redirects, asserts a 302, then requests its Location with
    // the same client (and so the same WebUI cookie) and returns the Location and the final body.
    private static async Task<(Uri Location, string Body)> FollowRedirectAsync(HttpClient client, Uri url)
    {
        using var redirect = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        var location = redirect.Headers.Location;
        Assert.NotNull(location);

        var target = new Uri(url, location);
        Assert.Equal(Query, target.Query);
        using var response = await client.GetAsync(target);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (location, await response.Content.ReadAsStringAsync());
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static WebUiFileHandlerResult TextResponse(string content)
    {
        var body = Encoding.UTF8.GetBytes(content);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "\r\n");
        return WebUiFileHandlerResult.FromResponse((byte[])[.. header, .. body]);
    }
}
