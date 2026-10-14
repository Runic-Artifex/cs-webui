namespace CsWebUi;

/// <summary>Represents a failure reported by the native WebUI runtime.</summary>
public class WebUiException : InvalidOperationException
{
    /// <summary>Initializes an exception from a native WebUI error.</summary>
    public WebUiException(nuint errorNumber, string? message = null)
        : base(message is { Length: > 0 } ? message : $"WebUI failed with error {errorNumber}.")
    {
        ErrorNumber = errorNumber;
    }

    /// <summary>Gets WebUI's native error number.</summary>
    public nuint ErrorNumber { get; }
}
