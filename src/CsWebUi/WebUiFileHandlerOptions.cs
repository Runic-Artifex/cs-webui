namespace CsWebUi;

/// <summary>Configures managed custom file-handler mechanics.</summary>
public sealed class WebUiFileHandlerOptions
{
    /// <summary>
    /// Gets or sets the largest complete raw HTTP response accepted from the handler.
    /// </summary>
    /// <remarks>The native WebUI ABI represents the response length as a signed 32-bit integer.</remarks>
    public int MaxResponseBytes { get; set; } = int.MaxValue;
}
