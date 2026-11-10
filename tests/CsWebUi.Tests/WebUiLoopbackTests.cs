using System.Net;
using System.Net.Sockets;
using System.Text;
using CsWebUi;
using CsWebUi.Internal;

namespace CsWebUi.Tests;

/// <summary>
/// WebUI listens on 127.0.0.1 only but opens http://localhost:port, which browsers try on [::1]
/// first. Another local process listening on [::1]:port would receive the window's page.
/// </summary>
public sealed class WebUiLoopbackTests
{
    private const string RealBody = "real window";

    [NativeIPv6Fact]
    public async Task StartServerSkipsAPortWhoseIPv6LoopbackIsHeldAndTheForeignServerGetsNothing()
    {
        // A foreign server on [::1]:A while 127.0.0.1:A is free: the layout WebUI's own port
        // check (127.0.0.1 only) accepted.
        using var foreign = ForeignServer.StartWithFreeIPv4Side();
        var offered = 0;
        WebUiWindow.PortCandidateSource = () => Interlocked.Increment(ref offered) == 1 ? foreign.Port : 0;
        try
        {
            using var window = new WebUiWindow();
            window.SetFileHandler(_ => TextResponse(RealBody));
            var url = new Uri(window.StartServer("index.html"));

            Assert.Equal("localhost", url.Host);
            var port = url.Port;

            // Both addresses a browser may use for localhost reach the window itself.
            Assert.Equal(RealBody, await GetAsync(IPAddress.IPv6Loopback, port));
            Assert.Equal(RealBody, await GetAsync(IPAddress.Loopback, port));
            Assert.Equal(0, foreign.Connections);

            Assert.True(offered >= 2, "The foreign server's port was not offered first.");
            Assert.NotEqual(foreign.Port, port);
            Assert.Equal(port, (int)window.Port);
            Assert.Equal(port, window.GuardedPort);

            // No other socket can listen on either address while the window runs.
            AssertCannotListen(IPAddress.IPv6Loopback, port);
            AssertCannotListen(IPAddress.Loopback, port);
        }
        finally
        {
            WebUiWindow.PortCandidateSource = null;
        }

        Assert.Equal(0, foreign.Connections);
    }

    [NativeIPv6Fact]
    public void SetPortRefusesAPortWhoseIPv6LoopbackIsHeld()
    {
        using var foreign = ForeignServer.StartWithFreeIPv4Side();
        using var window = new WebUiWindow();

        Assert.False(window.TrySetPort((nuint)foreign.Port));
        var exception = Assert.Throws<WebUiException>(() => window.SetPort((nuint)foreign.Port));
        Assert.Contains($"[::1]:{foreign.Port}", exception.Message);
        Assert.Equal(0, window.GuardedPort);
        Assert.Equal(0, foreign.Connections);
    }

    [NativeIPv6Fact]
    public async Task SetPortHoldsTheIPv6LoopbackUntilTheWindowIsDisposed()
    {
        var port = FindPortFreeOnBothLoopbacks();
        var window = new WebUiWindow();
        try
        {
            window.SetPort((nuint)port);
            Assert.Equal(port, window.GuardedPort);
            AssertCannotListen(IPAddress.IPv6Loopback, port);

            // Setting the same port again keeps the guard instead of colliding with it.
            Assert.True(window.TrySetPort((nuint)port));
            Assert.Equal(port, window.GuardedPort);

            window.SetFileHandler(_ => TextResponse(RealBody));
            var url = new Uri(window.StartServer("index.html"));
            Assert.Equal(port, url.Port);
            Assert.Equal(RealBody, await GetAsync(IPAddress.IPv6Loopback, port));
        }
        finally
        {
            window.Dispose();
        }

        using var after = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        after.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
        after.Listen();
    }

    [IPv6Fact]
    public async Task GuardRelaysBothDirectionsAndPassesOnHalfCloses()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        Assert.True(WebUiLoopbackGuard.TryCreate(port, out var guard));
        using var _ = guard;
        Assert.NotNull(guard);
        Assert.Equal(port, guard.Port);

        using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = server.AcceptSocketAsync(timeout.Token);
        await client.ConnectAsync(new IPEndPoint(IPAddress.IPv6Loopback, port), timeout.Token);
        using var accepted = await accept;

        // The server sees the relay's IPv4 loopback peer, as for a direct connection.
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)accepted.RemoteEndPoint!).Address);

        await client.SendAsync("request"u8.ToArray(), SocketFlags.None, timeout.Token);
        client.Shutdown(SocketShutdown.Send);
        Assert.Equal("request", await ReadToEndAsync(accepted, timeout.Token));

        // The client's half-close does not end the other direction.
        await accepted.SendAsync("response"u8.ToArray(), SocketFlags.None, timeout.Token);
        accepted.Shutdown(SocketShutdown.Send);
        Assert.Equal("response", await ReadToEndAsync(client, timeout.Token));
    }

    [IPv6Fact]
    public async Task GuardResetsAClientWhenNothingListensOnTheIPv4Side()
    {
        var port = FindPortFreeOnBothLoopbacks();
        Assert.True(WebUiLoopbackGuard.TryCreate(port, out var guard));
        using var _ = guard;

        using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new IPEndPoint(IPAddress.IPv6Loopback, port), timeout.Token);
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            var buffer = new byte[16];
            while (await client.ReceiveAsync(buffer, SocketFlags.None, timeout.Token) > 0)
            {
            }

            // An orderly end would let a browser treat the page as complete.
            throw new InvalidOperationException("The relay ended the connection instead of resetting it.");
        });
    }

    [IPv6Fact]
    public async Task DisposingTheGuardReleasesTheAddressAndResetsOpenConnections()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        Assert.True(WebUiLoopbackGuard.TryCreate(port, out var guard));
        Assert.NotNull(guard);

        using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = server.AcceptSocketAsync(timeout.Token);
        await client.ConnectAsync(new IPEndPoint(IPAddress.IPv6Loopback, port), timeout.Token);
        using var accepted = await accept;

        guard.Dispose();

        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            var buffer = new byte[16];
            while (await client.ReceiveAsync(buffer, SocketFlags.None, timeout.Token) > 0)
            {
            }

            throw new InvalidOperationException("The relay ended the connection instead of resetting it.");
        });

        using var after = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        after.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
        after.Listen();
    }

    internal static bool HasIPv6Loopback()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return false;
        }

        try
        {
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FindPortFreeOnBothLoopbacks()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            using var v4 = new TcpListener(IPAddress.Loopback, 0);
            v4.Start();
            var port = ((IPEndPoint)v4.LocalEndpoint).Port;
            try
            {
                using var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                v6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException("No port is free on both loopback addresses.");
    }

    // Tries what a squatter would: SO_REUSEADDR and, where it exists, SO_REUSEPORT.
    private static void AssertCannotListen(IPAddress address, int port)
    {
        using var squatter = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        squatter.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsLinux())
        {
            squatter.SetRawSocketOption(1, 15, BitConverter.GetBytes(1)); // SOL_SOCKET, SO_REUSEPORT
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            squatter.SetRawSocketOption(0xffff, 0x200, BitConverter.GetBytes(1)); // SOL_SOCKET, SO_REUSEPORT
        }

        var exception = Record.Exception(() =>
        {
            squatter.Bind(new IPEndPoint(address, port));
            squatter.Listen();
        });
        Assert.True(exception is SocketException, $"Another socket could listen on {address}:{port}.");
    }

    // Sends GET / with Host: localhost:port to one loopback address, as a browser does after
    // resolving localhost.
    private static async Task<string> GetAsync(IPAddress address, int port)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        return await client.GetStringAsync(new Uri($"http://localhost:{port}/"));
    }

    private static async Task<string> ReadToEndAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64];
        var text = new StringBuilder();
        int read;
        while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken)) > 0)
        {
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

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

    // Listens on [::1]:port only, answers every connection with its own page and counts them.
    private sealed class ForeignServer : IDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _stop = new();
        private int _connections;

        private ForeignServer(Socket listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            _ = ServeAsync();
        }

        internal int Port { get; }

        internal int Connections => Volatile.Read(ref _connections);

        internal static ForeignServer StartWithFreeIPv4Side()
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
                listener.Listen();
                var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
                try
                {
                    using var v4 = new TcpListener(IPAddress.Loopback, port);
                    v4.Start();
                    return new ForeignServer(listener);
                }
                catch (SocketException)
                {
                    listener.Dispose();
                }
            }

            throw new InvalidOperationException("No [::1] port with a free 127.0.0.1 side was found.");
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                using (client)
                {
                    try
                    {
                        await client.SendAsync(
                            "HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nforeign"u8.ToArray(),
                            SocketFlags.None);
                    }
                    catch (SocketException)
                    {
                    }
                }
            }
        }
    }
}

internal sealed class IPv6FactAttribute : FactAttribute
{
    public IPv6FactAttribute()
    {
        if (!WebUiLoopbackTests.HasIPv6Loopback())
        {
            Skip = "This machine has no IPv6 loopback address, so nothing can listen on [::1].";
        }
    }
}

internal sealed class NativeIPv6FactAttribute : FactAttribute
{
    public NativeIPv6FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY")))
        {
            Skip = NativeTestEnvironment.MissingLibraryReason;
        }
        else if (!WebUiLoopbackTests.HasIPv6Loopback())
        {
            Skip = "This machine has no IPv6 loopback address, so nothing can listen on [::1].";
        }
    }
}
