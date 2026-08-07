using System.Text;
using CsWebUi;

namespace CsWebUi.Tests;

public sealed class WebUiFileHandlerTests
{
    [Fact]
    public void ResultsDistinguishNotHandledFromCompleteResponses()
    {
        Assert.False(WebUiFileHandlerResult.NotHandled.IsHandled);
        Assert.True(WebUiFileHandlerResult.NotHandled.Response.IsEmpty);

        var bytes = "HTTP/1.1 204 No Content\r\n\r\n"u8.ToArray();
        var handled = WebUiFileHandlerResult.FromResponse(bytes);

        Assert.True(handled.IsHandled);
        Assert.Equal(bytes, handled.Response.ToArray());
        Assert.Throws<ArgumentException>(() =>
            WebUiFileHandlerResult.FromResponse(ReadOnlyMemory<byte>.Empty));
    }

    [NativeFact]
    public async Task NativeServerSupportsReplacementFailuresAndAsyncMode()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var window = new WebUiWindow();
        window.SetFileHandler(_ => TextResponse("first"));
        string serverUrl = window.StartServer("index.html");
        Assert.Equal("first", await client.GetStringAsync(serverUrl));

        window.SetFileHandler(_ => TextResponse("second"));
        Assert.Equal("second", await client.GetStringAsync(serverUrl));

        window.SetFileHandler(_ => throw new InvalidOperationException("contained"));
        using (HttpResponseMessage failure = await client.GetAsync(serverUrl))
        {
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, failure.StatusCode);
            Assert.Equal("Internal Server Error", await failure.Content.ReadAsStringAsync());
        }

        WebUiApplication.SetConfiguration(WebUiConfiguration.AsynchronousResponse, true);
        try
        {
            window.SetFileHandler(_ => TextResponse("async"));
            Assert.Equal("async", await client.GetStringAsync(serverUrl));
        }
        finally
        {
            WebUiApplication.SetConfiguration(WebUiConfiguration.AsynchronousResponse, false);
        }
    }

    [NativeFact]
    public async Task ResponseLimitProducesAContainedServerError()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var window = new WebUiWindow();
        window.SetFileHandler(
            _ => TextResponse("too large"),
            new WebUiFileHandlerOptions { MaxResponseBytes = 1 });
        string serverUrl = window.StartServer("index.html");

        using HttpResponseMessage response = await client.GetAsync(serverUrl);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static WebUiFileHandlerResult TextResponse(string content)
    {
        byte[] body = Encoding.UTF8.GetBytes(content);
        byte[] header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "\r\n");
        var response = new byte[header.Length + body.Length];
        header.CopyTo(response, 0);
        body.CopyTo(response, header.Length);
        return WebUiFileHandlerResult.FromResponse(response);
    }
}
