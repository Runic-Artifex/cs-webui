namespace CsWebUi;

/// <summary>Handles one cleaned, URL-decoded path requested by WebUI.</summary>
/// <param name="path">The path supplied by WebUI.</param>
/// <returns>A complete raw HTTP response, or <see cref="WebUiFileHandlerResult.NotHandled"/>.</returns>
public delegate WebUiFileHandlerResult WebUiFileHandler(string path);

/// <summary>Represents the result of a managed WebUI file-handler callback.</summary>
public readonly struct WebUiFileHandlerResult
{
    private readonly ReadOnlyMemory<byte> _response;

    private WebUiFileHandlerResult(ReadOnlyMemory<byte> response)
    {
        _response = response;
        IsHandled = true;
    }

    /// <summary>
    /// Gets a result that allows WebUI to continue with its configured local-file handling.
    /// </summary>
    public static WebUiFileHandlerResult NotHandled => default;

    /// <summary>Gets whether this result contains a complete raw HTTP response.</summary>
    public bool IsHandled { get; }

    /// <summary>Gets the complete raw HTTP response when <see cref="IsHandled"/> is true.</summary>
    public ReadOnlyMemory<byte> Response => _response;

    /// <summary>Creates a handled result from complete raw HTTP header and body bytes.</summary>
    /// <exception cref="ArgumentException"><paramref name="response"/> is empty.</exception>
    public static WebUiFileHandlerResult FromResponse(ReadOnlyMemory<byte> response)
    {
        if (response.IsEmpty)
        {
            throw new ArgumentException("A handled file response cannot be empty.", nameof(response));
        }

        return new WebUiFileHandlerResult(response);
    }
}
