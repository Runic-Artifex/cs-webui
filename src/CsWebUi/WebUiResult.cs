namespace CsWebUi;

/// <summary>Represents a value returned from a managed WebUI binding to JavaScript.</summary>
public readonly struct WebUiResult
{
    private readonly object? _value;

    private WebUiResult(WebUiResultKind kind, object? value)
    {
        Kind = kind;
        _value = value;
    }

    /// <summary>Gets an empty response.</summary>
    public static WebUiResult None { get; } = new(WebUiResultKind.None, null);

    /// <summary>Gets the kind of response represented by this result.</summary>
    public WebUiResultKind Kind { get; }

    /// <summary>Creates an integer response.</summary>
    public static WebUiResult FromInt64(long value) => new(WebUiResultKind.Int64, value);

    /// <summary>Creates a floating-point response.</summary>
    public static WebUiResult FromDouble(double value) => new(WebUiResultKind.Double, value);

    /// <summary>Creates a Boolean response.</summary>
    public static WebUiResult FromBoolean(bool value) => new(WebUiResultKind.Boolean, value);

    /// <summary>Creates a UTF-8 string response.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> contains a null character. WebUI sends a reply as a C string, which
    /// would end there.
    /// </exception>
    public static WebUiResult FromString(string? value)
    {
        if (value is not null && value.Contains('\0'))
        {
            throw new ArgumentException("Embedded null characters are not supported by the WebUI C string API.", nameof(value));
        }

        return new(WebUiResultKind.String, value ?? string.Empty);
    }

    /// <summary>Creates a string result implicitly.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains a null character.</exception>
    public static implicit operator WebUiResult(string value) => FromString(value);

    /// <summary>Creates an integer result implicitly.</summary>
    public static implicit operator WebUiResult(long value) => FromInt64(value);

    /// <summary>Creates a floating-point result implicitly.</summary>
    public static implicit operator WebUiResult(double value) => FromDouble(value);

    /// <summary>Creates a Boolean result implicitly.</summary>
    public static implicit operator WebUiResult(bool value) => FromBoolean(value);

    internal long Int64Value => _value is long value ? value : default;

    internal double DoubleValue => _value is double value ? value : default;

    internal bool BooleanValue => _value is bool value && value;

    internal string StringValue => _value as string ?? string.Empty;
}

/// <summary>Identifies the native response function used for a <see cref="WebUiResult"/>.</summary>
public enum WebUiResultKind
{
    /// <summary>Returns an empty string response.</summary>
    None,

    /// <summary>Returns a signed 64-bit integer.</summary>
    Int64,

    /// <summary>Returns an IEEE 754 double.</summary>
    Double,

    /// <summary>Returns a Boolean value.</summary>
    Boolean,

    /// <summary>Returns a UTF-8 string.</summary>
    String,
}
