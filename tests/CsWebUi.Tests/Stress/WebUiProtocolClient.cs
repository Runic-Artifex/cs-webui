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
/// <c>[function]\0[len;len;...]\0[arg]\0[arg]\0...</c>.
/// </remarks>
internal sealed partial class WebUiProtocolClient : IAsyncDisposable
{
    private const byte Signature = 0xDD;
    private const byte CallFunction = 0xF9;

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

    /// <summary>Sends one backend call; WebUI processes each message on its own thread.</summary>
    internal async Task CallAsync(string function, string argument, CancellationToken cancellationToken)
    {
        var id = (ushort)Interlocked.Increment(ref _nextId);
        var name = Encoding.UTF8.GetBytes(function);
        var value = Encoding.UTF8.GetBytes(argument);
        var lengths = Encoding.ASCII.GetBytes(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var packet = new byte[8 + name.Length + 1 + lengths.Length + 1 + value.Length + 1];
        packet[0] = Signature;
        BitConverter.TryWriteBytes(packet.AsSpan(1, 4), _token);
        BitConverter.TryWriteBytes(packet.AsSpan(5, 2), id);
        if (!BitConverter.IsLittleEndian)
        {
            packet.AsSpan(1, 4).Reverse();
            packet.AsSpan(5, 2).Reverse();
        }

        packet[7] = CallFunction;
        var offset = 8;
        name.CopyTo(packet, offset);
        offset += name.Length + 1;
        lengths.CopyTo(packet, offset);
        offset += lengths.Length + 1;
        value.CopyTo(packet, offset);

        await _socket.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
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
