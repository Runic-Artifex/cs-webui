using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CsWebUi.Tests.Stress;

/// <summary>
/// A minimal WebUI bridge client: it loads <c>webui.js</c> for the window token and auth cookie,
/// connects to the window's WebSocket and sends backend function calls the way <c>webui.js</c> does.
/// </summary>
/// <remarks>
/// Wire format (webui.c and bridge/webui.ts at the pinned revision): an 8-byte header
/// <c>[0xDD][token: u32 LE][id: u16 LE][cmd]</c>, then for a call
/// <c>[function]\0[len;len;...]\0[arg]\0[arg]\0...</c>. WebUI's messages use the same header,
/// followed by their data and one null byte.
/// </remarks>
internal sealed partial class WebUiProtocolClient : IAsyncDisposable
{
    private const byte Signature = 0xDD;
    private const byte CallFunction = 0xF9;
    private const byte Navigation = 0xFB;
    private const byte CheckToken = 0xF5;
    private const byte JavaScript = 0xFE;
    private const int HeaderSize = 8;

    private readonly ClientWebSocket _socket;
    private readonly uint _token;
    private int _nextId;

    private WebUiProtocolClient(ClientWebSocket socket, uint token)
    {
        _socket = socket;
        _token = token;
    }

    internal static async Task<WebUiProtocolClient> ConnectAsync(Uri serverUrl, CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var script = await http.GetStringAsync(new Uri(serverUrl, "/webui.js"), cancellationToken).ConfigureAwait(false);
        var match = TokenPattern().Match(script);
        if (!match.Success)
        {
            throw new InvalidOperationException("webui.js did not contain a window token.");
        }

        var token = uint.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        var builder = new UriBuilder(serverUrl) { Scheme = "ws", Path = "/_webui_ws_connect" };
        try
        {
            await socket.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new WebUiProtocolClient(socket, token);
    }

    /// <summary>
    /// Presents the window token as webui.js does after connecting. WebUI sends replies only to
    /// a client whose token it accepted; calls are processed either way.
    /// </summary>
    internal async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var packet = new byte[HeaderSize + 1];
        WriteHeader(packet, 0, CheckToken);
        await _socket.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        _ = await ReceiveAsync(CheckToken, 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Receives the data of the reply to a call, exactly as WebUI framed it.</summary>
    internal async Task<byte[]> ReceiveReplyAsync(ushort id, CancellationToken cancellationToken)
        => (await ReceiveAsync(CallFunction, id, cancellationToken).ConfigureAwait(false)).Data;

    /// <summary>Receives the next script WebUI runs in this client (<c>webui_script</c>) and its run id.</summary>
    internal async Task<(ushort Id, string Script)> ReceiveScriptAsync(CancellationToken cancellationToken)
    {
        var (id, data) = await ReceiveAsync(JavaScript, null, cancellationToken).ConfigureAwait(false);
        return (id, Encoding.UTF8.GetString(data));
    }

    /// <summary>Sends a script result as webui.js does: <c>[header][error][result]\0</c>.</summary>
    internal async Task SendScriptResultAsync(ushort id, ReadOnlyMemory<byte> result, CancellationToken cancellationToken)
    {
        var packet = new byte[HeaderSize + 1 + result.Length + 1];
        WriteHeader(packet, id, JavaScript);
        result.Span.CopyTo(packet.AsSpan(HeaderSize + 1));
        await _socket.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reports a navigation as webui.js does: <c>[header][url]\0</c>.</summary>
    internal async Task SendNavigationAsync(string url, CancellationToken cancellationToken)
    {
        var value = Encoding.UTF8.GetBytes(url);
        var packet = new byte[HeaderSize + value.Length + 1];
        WriteHeader(packet, 0, Navigation);
        value.CopyTo(packet, HeaderSize);
        await _socket.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a call with an arbitrary length list, which a well-behaved client derives from its arguments.</summary>
    /// <returns>The call's id, which its reply carries.</returns>
    internal async Task<ushort> SendCallAsync(string function, string lengths, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken)
    {
        var id = (ushort)Interlocked.Increment(ref _nextId);
        var name = Encoding.UTF8.GetBytes(function);
        var lengthBytes = Encoding.ASCII.GetBytes(lengths);
        var packet = new byte[HeaderSize + name.Length + 1 + lengthBytes.Length + 1 + arguments.Length];
        WriteHeader(packet, id, CallFunction);
        name.CopyTo(packet, HeaderSize);
        lengthBytes.CopyTo(packet, HeaderSize + name.Length + 1);
        arguments.Span.CopyTo(packet.AsSpan(HeaderSize + name.Length + 1 + lengthBytes.Length + 1));
        await _socket.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>Sends one backend call; WebUI processes each message on its own thread.</summary>
    /// <returns>The call's id, which its reply carries.</returns>
    internal async Task<ushort> CallAsync(string function, string argument, CancellationToken cancellationToken)
    {
        var id = (ushort)Interlocked.Increment(ref _nextId);
        var name = Encoding.UTF8.GetBytes(function);
        var value = Encoding.UTF8.GetBytes(argument);
        var lengths = Encoding.ASCII.GetBytes(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var packet = new byte[HeaderSize + name.Length + 1 + lengths.Length + 1 + value.Length + 1];
        WriteHeader(packet, id, CallFunction);
        var offset = HeaderSize;
        name.CopyTo(packet, offset);
        offset += name.Length + 1;
        lengths.CopyTo(packet, offset);
        offset += lengths.Length + 1;
        value.CopyTo(packet, offset);

        await _socket.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        return id;
    }

    private void WriteHeader(byte[] packet, ushort id, byte command)
    {
        packet[0] = Signature;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), _token);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), id);
        packet[7] = command;
    }

    /// <summary>Receives messages until one with this command (and id, if given) arrives, and returns its data.</summary>
    private async Task<(ushort Id, byte[] Data)> ReceiveAsync(byte command, ushort? id, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("WebUI closed the connection.");
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var packet = message.ToArray();
            if (packet.Length > HeaderSize && packet[0] == Signature && packet[7] == command)
            {
                var packetId = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(5, 2));
                if (id is { } expected && packetId != expected)
                {
                    continue;
                }

                if (packet[^1] != 0)
                {
                    throw new InvalidDataException("WebUI's message does not end with its null byte.");
                }

                return (packetId, packet[HeaderSize..^1]);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        // The server goes away during these tests; abort instead of a close handshake.
        _socket.Abort();
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex(@"token:\s*(\d+)")]
    private static partial Regex TokenPattern();
}
