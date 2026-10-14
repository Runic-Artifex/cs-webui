using System.Text;
using CsWebUi.Internal;
using CsWebUi.Native;

namespace CsWebUi;

/// <summary>Represents one native WebUI event while its managed callback is executing.</summary>
/// <remarks>
/// Argument access and client operations are valid only while the callback is active. An event
/// passed to <see cref="WebUiWindow.BindAsync"/> stays active until its returned operation ends,
/// unless <see cref="WebUiApplication.Exit"/> or <see cref="WebUiApplication.Clean"/> runs first:
/// WebUI then stops waiting for the reply and releases the event, so its arguments and client can
/// only be reached from the binding's synchronous part. Once a reply has been sent, WebUI frees the
/// event shortly afterwards, so every later member that reaches the native event throws
/// <see cref="InvalidOperationException"/>, on any thread.
/// </remarks>
public sealed unsafe class WebUiEvent
{
    /// <summary>WebUI's WEBUI_MAX_BUF: the largest message WebUI receives, and so the largest argument.</summary>
    private const nuint MaxArgumentBytes = 64_000_000;

    private readonly WebUiWindow _window;
    private readonly WebUiEventNative* _nativeEvent;
    private readonly int _generation;
    private readonly int _dispatchThreadId;
    private int _dispatching = 1;
    private int _active = 1;
    private int _responded;
    private int _abandoned;
    private int _accessors;

    internal WebUiEvent(WebUiWindow window, WebUiEventNative* nativeEvent)
    {
        _window = window;
        _nativeEvent = nativeEvent;
        _generation = WebUiNativeLifetime.Generation;
        _dispatchThreadId = Environment.CurrentManagedThreadId;

        WindowId = nativeEvent->Window;
        EventType = (WebUiEventType)(uint)nativeEvent->EventType;
        Element = Utf8.DecodeRequired(nativeEvent->Element);
        EventNumber = nativeEvent->EventNumber;
        BindingId = nativeEvent->BindId;
        ClientId = nativeEvent->ClientId;
        ConnectionId = nativeEvent->ConnectionId;
        Cookies = Utf8.DecodeRequired(nativeEvent->Cookies);
    }

    /// <summary>Gets the window that received this event.</summary>
    public WebUiWindow Window => _window;

    /// <summary>Gets the native window identifier.</summary>
    public nuint WindowId { get; }

    /// <summary>Gets the event's category.</summary>
    public WebUiEventType EventType { get; }

    /// <summary>Gets the bound element or JavaScript object name.</summary>
    public string Element { get; }

    /// <summary>Gets WebUI's event identifier.</summary>
    public nuint EventNumber { get; }

    /// <summary>Gets WebUI's binding identifier.</summary>
    public nuint BindingId { get; }

    /// <summary>Gets the browser client identifier.</summary>
    public nuint ClientId { get; }

    /// <summary>Gets the client's current connection identifier.</summary>
    public nuint ConnectionId { get; }

    /// <summary>Gets the complete cookie header sent by the client.</summary>
    public string Cookies { get; }

    /// <summary>Gets the number of arguments sent with this event.</summary>
    public nuint ArgumentCount
    {
        get
        {
            using var scope = EnterNative();
            return WebUiNative.GetCount(_nativeEvent);
        }
    }

    /// <summary>Gets an argument as a signed 64-bit integer.</summary>
    /// <exception cref="FormatException">The argument is not a JavaScript-compatible 64-bit integer.</exception>
    /// <exception cref="InvalidDataException">The call is malformed: an argument claims more bytes than WebUI accepts in a message.</exception>
    public long GetInt64(nuint index = 0)
    {
        using var scope = EnterNative();
        return WebUiArgumentParser.ParseInt64(ReadStringArgument(index));
    }

    /// <summary>Gets an argument as a double-precision floating-point number.</summary>
    /// <exception cref="FormatException">The argument is not a JavaScript-compatible floating-point value.</exception>
    /// <exception cref="InvalidDataException">The call is malformed: an argument claims more bytes than WebUI accepts in a message.</exception>
    public double GetDouble(nuint index = 0)
    {
        using var scope = EnterNative();
        return WebUiArgumentParser.ParseDouble(ReadStringArgument(index));
    }

    /// <summary>Gets an argument as a Boolean value.</summary>
    /// <exception cref="FormatException">The argument is not a JavaScript-compatible Boolean value.</exception>
    /// <exception cref="InvalidDataException">The call is malformed: an argument claims more bytes than WebUI accepts in a message.</exception>
    public bool GetBoolean(nuint index = 0)
    {
        using var scope = EnterNative();
        return WebUiArgumentParser.ParseBoolean(ReadStringArgument(index));
    }

    /// <summary>Gets an argument as a UTF-8 string.</summary>
    /// <remarks>
    /// The string is read with the byte length WebUI received, so null characters sent by
    /// JavaScript are preserved rather than ending the string early.
    /// </remarks>
    /// <exception cref="InvalidDataException">The call is malformed: an argument claims more bytes than WebUI accepts in a message.</exception>
    public string GetString(nuint index = 0)
    {
        using var scope = EnterNative();
        return ReadStringArgument(index);
    }

    /// <summary>Copies an argument's raw bytes into managed memory.</summary>
    /// <exception cref="InvalidDataException">The call is malformed: an argument claims more bytes than WebUI accepts in a message.</exception>
    public byte[] GetBytes(nuint index = 0)
    {
        using var scope = EnterNative();
        return ReadArgument(index).ToArray();
    }

    /// <summary>Sends JavaScript to only the client that raised this event.</summary>
    public void RunJavaScript(string script)
    {
        using var scope = EnterNative();
        var bytes = Utf8.Encode(script, nameof(script));
        fixed (byte* value = bytes)
        {
            WebUiNative.RunClient(_nativeEvent, value);
        }
    }

    /// <summary>Navigates only the client that raised this event.</summary>
    public void Navigate(string url)
    {
        using var scope = EnterNative();
        var bytes = Utf8.Encode(url, nameof(url));
        fixed (byte* value = bytes)
        {
            WebUiNative.NavigateClient(_nativeEvent, value);
        }
    }

    /// <summary>Shows content to only the client that raised this event.</summary>
    /// <exception cref="WebUiException">The native runtime could not show the content.</exception>
    public void Show(string content)
    {
        using var scope = EnterNative();
        var bytes = Utf8.Encode(content, nameof(content));
        fixed (byte* value = bytes)
        {
            if (WebUiNative.ShowClient(_nativeEvent, value) == 0)
            {
                throw WebUiApplication.CreateNativeException();
            }
        }
    }

    /// <summary>Sends raw bytes to only the client that raised this event.</summary>
    public void SendRaw(string function, ReadOnlySpan<byte> data)
    {
        using var scope = EnterNative();
        var functionBytes = Utf8.Encode(function, nameof(function));
        fixed (byte* functionValue = functionBytes)
        fixed (byte* dataValue = data)
        {
            WebUiNative.SendRawClient(_nativeEvent, functionValue, dataValue, (nuint)data.Length);
        }
    }

    /// <summary>Closes only the client that raised this event.</summary>
    public void CloseClient()
    {
        using var scope = EnterNative();
        WebUiNative.CloseClient(_nativeEvent);
    }

    /// <summary>Sends a response to the JavaScript caller.</summary>
    /// <exception cref="InvalidOperationException">A response was already sent or the callback is no longer active.</exception>
    public void Respond(WebUiResult result)
    {
        ThrowIfInactive();
        if (!TryRespond(result))
        {
            if (Volatile.Read(ref _abandoned) != 0)
            {
                throw CreateAbandonedException();
            }

            throw CreateAnsweredException();
        }
    }

    /// <summary>Sends the reply unless one was already sent or WebUI has abandoned the event.</summary>
    internal bool TryRespond(WebUiResult result)
    {
        if (Interlocked.CompareExchange(ref _responded, 1, 0) != 0)
        {
            return false;
        }

        // From here on new accesses throw. Accesses already running on other threads must finish
        // before the reply: once it is sent, WebUI stops waiting and frees the event.
        var spinner = default(SpinWait);
        while (Volatile.Read(ref _accessors) != 0)
        {
            spinner.SpinOnce();
        }

        if (!TryEnterNative(out var gated))
        {
            // WebUI already answered the call and released the event; there is nothing left to
            // reply to. The event stays marked as responded so no fallback reply is attempted.
            Volatile.Write(ref _abandoned, 1);
            return false;
        }

        try
        {
            switch (result.Kind)
            {
                case WebUiResultKind.None:
                    ReturnString(string.Empty);
                    break;
                case WebUiResultKind.Int64:
                    WebUiNative.ReturnInt(_nativeEvent, result.Int64Value);
                    break;
                case WebUiResultKind.Double:
                    // WebUI's native webui_return_float() uses the process locale and
                    // formats a fixed six decimal places. The JavaScript bridge returns
                    // all responses as text, so a culture-invariant round-trip string is
                    // safer and preserves the actual double value for high-level callers.
                    ReturnString(result.DoubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case WebUiResultKind.Boolean:
                    WebUiNative.ReturnBool(_nativeEvent, WebUiApplication.NativeBoolean(result.BooleanValue));
                    break;
                case WebUiResultKind.String:
                    ReturnString(result.StringValue);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result));
            }

            return true;
        }
        catch
        {
            // No native response was issued. Keep the event available so the
            // dispatcher can send its fallback reply rather than leave JS waiting.
            Volatile.Write(ref _responded, 0);
            throw;
        }
        finally
        {
            if (gated)
            {
                WebUiNativeLifetime.Leave();
            }
        }
    }

    internal void Invalidate() => Volatile.Write(ref _active, 0);

    /// <summary>Marks the end of the native callback; later access must go through the lifetime gate.</summary>
    internal void EndDispatch() => Volatile.Write(ref _dispatching, 0);

    private void ReturnString(string value)
    {
        var bytes = Utf8.Encode(value, nameof(value));
        fixed (byte* response = bytes)
        {
            if (value.Length == 0)
            {
                // Before WebUI 4e65ad711, webui_return_string() exited early for an
                // empty C string and, with asynchronous_response enabled, never marked
                // the event done. The wrapper-interface setter reaches the same event
                // state and completes an intentionally empty response in every version.
                WebUiNative.InterfaceSetResponse(WindowId, EventNumber, response);
            }
            else
            {
                WebUiNative.ReturnString(_nativeEvent, response);
            }
        }
    }

    private string ReadStringArgument(nuint index) => Encoding.UTF8.GetString(ReadArgument(index));

    /// <summary>Gets an argument's bytes, bounded by the size WebUI received; valid inside a native scope.</summary>
    private ReadOnlySpan<byte> ReadArgument(nuint index)
    {
        var count = WebUiNative.GetCount(_nativeEvent);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(index));

        // WebUI before 2456744e6 checks a call's argument lengths only by their sum, which wraps
        // in size_t: a malformed message can claim an argument longer than the bytes it carries if
        // another claims nearly SIZE_MAX. WebUI receives at most WEBUI_MAX_BUF bytes per message,
        // so no genuine argument is longer; with every length within it the sum cannot wrap, and
        // WebUI's check then guarantees the bytes are there. Since 2456744e6 (webui-dev/webui#722)
        // WebUI checks each length against the bytes left and drops such a call itself, so this
        // check is redundant with that WebUI; it is kept for older builds.
        for (nuint i = 0; i < count; i++)
        {
            if (WebUiNative.GetSizeAt(_nativeEvent, i) > MaxArgumentBytes)
            {
                throw new InvalidDataException("The WebUI call is malformed: an argument claims more bytes than WebUI accepts in a message.");
            }
        }

        var size = WebUiNative.GetSizeAt(_nativeEvent, index);
        if (size == 0)
        {
            return [];
        }

        var source = WebUiNative.GetStringAt(_nativeEvent, index);
        return source is null ? [] : new ReadOnlySpan<byte>(source, (int)size);
    }

    private NativeScope EnterNative()
    {
        ThrowIfInactive();

        // A Dekker handshake with TryRespond: either the reply waits for this access, or this
        // access sees the reply and stays out.
        Interlocked.Increment(ref _accessors);
        if (Volatile.Read(ref _responded) != 0)
        {
            Interlocked.Decrement(ref _accessors);
            throw CreateAnsweredException();
        }

        if (!TryEnterNative(out var gated))
        {
            Interlocked.Decrement(ref _accessors);
            throw CreateAbandonedException();
        }

        return new NativeScope(this, gated);
    }

    private bool TryEnterNative(out bool gated)
    {
        // The thread still inside the native callback keeps the event's stack frame alive.
        if (Volatile.Read(ref _dispatching) != 0 && Environment.CurrentManagedThreadId == _dispatchThreadId)
        {
            gated = false;
            return true;
        }

        gated = true;
        return WebUiNativeLifetime.TryEnter(_generation);
    }

    private static InvalidOperationException CreateAnsweredException()
        => new("This WebUI event was already answered. WebUI releases an event once its reply is sent, so its arguments and client can no longer be reached; copy what you need before replying.");

    private static InvalidOperationException CreateAbandonedException()
        => new("WebUI is exiting or was cleaned and has released this event; its arguments and client can no longer be reached.");

    private void ThrowIfInactive()
    {
        if (Volatile.Read(ref _active) == 0)
        {
            throw new InvalidOperationException("This WebUI event is no longer active. Copy the values needed by background work before the callback returns.");
        }
    }

    private readonly ref struct NativeScope(WebUiEvent owner, bool gated)
    {
        public void Dispose()
        {
            if (gated)
            {
                WebUiNativeLifetime.Leave();
            }

            Interlocked.Decrement(ref owner._accessors);
        }
    }
}
