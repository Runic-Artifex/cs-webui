using CsWebUi;

namespace CsWebUi.Tests;

public sealed class WebUiStartServerTests
{
    [Fact]
    public void ReturnsAnAbsoluteNativeUrlWithoutReadingTheNativeError()
    {
        var url = WebUiWindow.RequireServerUrl(
            "http://localhost:43210",
            7,
            static () => throw new InvalidOperationException("The native error must not be read."));

        Assert.Equal("http://localhost:43210", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyNativeUrlThrowsWithTheNativeError(string? nativeUrl)
    {
        var exception = Assert.Throws<WebUiException>(() => WebUiWindow.RequireServerUrl(
            nativeUrl,
            7,
            static () => (42, "Listening failed.")));

        Assert.Equal((nuint)42, exception.ErrorNumber);
        Assert.Contains("window 7", exception.Message);
        Assert.Contains("an empty URL", exception.Message);
        Assert.Contains("WebUI error 42: Listening failed.", exception.Message);
    }

    [Fact]
    public void EmptyNativeUrlStillThrowsWhenWebUiReportsNoErrorDetails()
    {
        var exception = Assert.Throws<WebUiException>(() => WebUiWindow.RequireServerUrl(
            string.Empty,
            3,
            static () => (0, string.Empty)));

        Assert.Equal((nuint)0, exception.ErrorNumber);
        Assert.Contains("an empty URL", exception.Message);
        Assert.Contains("WebUI reported no error details (error 0).", exception.Message);
    }

    [Fact]
    public void RelativeNativeUrlThrows()
    {
        var exception = Assert.Throws<WebUiException>(() => WebUiWindow.RequireServerUrl(
            "localhost-without-scheme",
            3,
            static () => (0, null)));

        Assert.Contains("the invalid URL 'localhost-without-scheme'", exception.Message);
    }
}
