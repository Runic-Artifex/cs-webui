using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CsWebUi.Internal;

/// <summary>
/// Holds <c>[::1]:port</c> for a window whose WebUI server listens only on <c>127.0.0.1:port</c>,
/// and relays every connection made to it to that server.
/// </summary>
/// <remarks>
/// <para>
/// WebUI opens its pages at <c>http://localhost:port</c> but listens on <c>127.0.0.1</c> only.
/// Browsers resolve <c>localhost</c> to <c>::1</c> as well, try it first and fall back to
/// <c>127.0.0.1</c> only when <c>::1</c> refuses the connection. Without this guard another local
/// process could listen on <c>[::1]:port</c> and serve or proxy the whole page.
/// </para>
/// <para>
/// The guard is a listening socket that never sets <c>SO_REUSEPORT</c>, so no other socket can
/// listen on the address while it exists. .NET sets <c>SO_REUSEADDR</c> on every TCP socket it
/// binds on Linux and macOS; that does not matter here. On Linux a listening socket keeps every
/// other bind off its address unless both sockets set <c>SO_REUSEPORT</c>, and on macOS and the
/// BSDs a second bind to the same address needs <c>SO_REUSEPORT</c> on both sockets as well. On
/// Windows the guard sets <c>SO_EXCLUSIVEADDRUSE</c> before it binds, which no
/// <c>SO_REUSEADDR</c> socket can override.
/// </para>
/// <para>
/// It forwards each connection byte for byte, so the page, its WebSocket and WebUI's own checks
/// behave exactly as for a direct IPv4 connection. Half-closes are passed on; a failure on either
/// side resets both. At most <see cref="DefaultMaxConnections"/> connections are relayed at once;
/// the guard resets further ones until one ends, and reports that once per burst.
/// </para>
/// </remarks>
internal sealed class WebUiLoopbackGuard : IDisposable
{
    // A page uses a handful of connections: the document, its scripts and the WebUI WebSocket.
    // Each relayed connection costs two sockets and two buffers, so the cap keeps a local process
    // from using up the application's file descriptors through the guard.
    internal const int DefaultMaxConnections = 256;

    private const int BufferSize = 16 * 1024;
    private static readonly TimeSpan MinAcceptBackoff = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReleasePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Socket _listener;
    private readonly IPEndPoint _target;
    private readonly int _maxConnections;
    private readonly CancellationTokenSource _closed = new();
    private readonly HashSet<Socket> _connections = [];
    private readonly object _gate = new();
    private int _relays;
    private bool _capReported;
    private bool _disposed;

    private WebUiLoopbackGuard(Socket listener, int port, int maxConnections)
    {
        _listener = listener;
        _target = new IPEndPoint(IPAddress.Loopback, port);
        _maxConnections = maxConnections;
        Port = port;
        _ = AcceptLoopAsync();
    }

    /// <summary>Gets the guarded port.</summary>
    internal int Port { get; }

    /// <summary>Attempts to hold <c>[::1]:port</c>.</summary>
    /// <param name="port">The port WebUI's IPv4 loopback server uses, or zero for any free port.</param>
    /// <param name="guard">The guard, or <see langword="null"/> when none is needed or possible.</param>
    /// <returns>
    /// <see langword="true"/> when nothing else can listen on <c>[::1]:port</c>: the guard holds it,
    /// or this machine has no IPv6 loopback at all. <see langword="false"/> when another socket
    /// already holds the address, so the port must not be used.
    /// </returns>
    /// <exception cref="SocketException">
    /// No socket could be created for another reason, for example because the process has no file
    /// descriptors left.
    /// </exception>
    internal static bool TryCreate(int port, out WebUiLoopbackGuard? guard)
        => TryCreate(port, DefaultMaxConnections, out guard);

    internal static bool TryCreate(int port, int maxConnections, out WebUiLoopbackGuard? guard)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        guard = null;
        if (!Socket.OSSupportsIPv6)
        {
            return true;
        }

        Socket listener;
        try
        {
            listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        }
        catch (SocketException exception) when (IsMissingIPv6Loopback(exception.SocketErrorCode))
        {
            return true;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Without SO_EXCLUSIVEADDRUSE another socket that sets SO_REUSEADDR could bind it too.
                listener.ExclusiveAddressUse = true;
            }

            listener.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
            listener.Listen(128);
        }
        catch (SocketException exception)
        {
            listener.Dispose();
            // Nothing can listen on ::1 without an IPv6 loopback, and no browser can connect there.
            return IsMissingIPv6Loopback(exception.SocketErrorCode);
        }

        // Port zero lets the operating system choose; the guard reports the port it holds.
        guard = new WebUiLoopbackGuard(listener, ((IPEndPoint)listener.LocalEndPoint!).Port, maxConnections);
        return true;
    }

    /// <summary>
    /// Disposes the guard once nothing listens on <c>127.0.0.1</c> at its port any more.
    /// </summary>
    /// <remarks>
    /// Waits on the thread pool, never on the caller, which may be WebUI's callback thread that
    /// the server's shutdown waits for. When the port stays taken beyond <paramref name="limit"/>,
    /// the guard is kept for the rest of the process, so <c>[::1]</c> is never free while WebUI's
    /// server may still listen.
    /// </remarks>
    /// <returns>A task that completes when the guard is disposed or kept for good.</returns>
    internal Task DisposeWhenServerStopped(TimeSpan limit)
    {
        if (!WebUiWindow.IsPortTaken(Port))
        {
            Dispose();
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            var delay = TimeSpan.FromMilliseconds(10);
            while (WebUiWindow.IsPortTaken(Port))
            {
                if (Stopwatch.GetElapsedTime(started) >= limit)
                {
                    WebUiApplication.ReportProblem(string.Create(
                        CultureInfo.InvariantCulture,
                        $"127.0.0.1:{Port} is still in use {limit.TotalSeconds:0} s after its window was destroyed; CS-WebUI keeps holding [::1]:{Port}."));
                    return;
                }

                await Task.Delay(delay).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReleasePollInterval.Ticks));
            }

            Dispose();
        });
    }

    /// <summary>Stops listening and resets every relayed connection.</summary>
    public void Dispose()
    {
        Socket[] connections;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connections = [.. _connections];
            _connections.Clear();
        }

        _closed.Cancel();
        _listener.Dispose();
        foreach (var connection in connections)
        {
            Abort(connection);
        }

        // _closed is not disposed: relays that are still ending observe its token. It holds no
        // timer or other unmanaged state.
    }

    private static bool IsMissingIPv6Loopback(SocketError error)
        => error is SocketError.AddressNotAvailable
            or SocketError.AddressFamilyNotSupported
            or SocketError.ProtocolNotSupported
            or SocketError.ProtocolFamilyNotSupported;

    private async Task AcceptLoopAsync()
    {
        var token = _closed.Token;
        var backoff = TimeSpan.Zero;
        while (true)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                {
                    // A connection that failed before it was accepted; keep serving the others.
                    continue;
                }

                // Anything else, such as running out of file descriptors, fails again at once.
                // Wait instead of spinning, and keep the address held.
                backoff = backoff == TimeSpan.Zero
                    ? MinAcceptBackoff
                    : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));
                try
                {
                    await Task.Delay(backoff, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            backoff = TimeSpan.Zero;
            if (!TryBeginRelay())
            {
                Abort(client);
                continue;
            }

            _ = RelayAsync(client, token);
        }
    }

    private bool TryBeginRelay()
    {
        bool report;
        lock (_gate)
        {
            if (_relays < _maxConnections)
            {
                _relays++;
                return true;
            }

            report = !_capReported;
            _capReported = true;
        }

        if (report)
        {
            WebUiApplication.ReportProblem(
                $"CS-WebUI relays at most {_maxConnections} connections at once to the server of port {Port}; it resets further connections until one ends.");
        }

        return false;
    }

    private void EndRelay()
    {
        lock (_gate)
        {
            _relays--;
            if (_relays < _maxConnections / 2)
            {
                // Report again if the cap is reached after the load has eased.
                _capReported = false;
            }
        }
    }

    private async Task RelayAsync(Socket client, CancellationToken token)
    {
        Socket? server = null;
        try
        {
            if (!Track(client))
            {
                Abort(client);
                return;
            }

            server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            client.NoDelay = true;
            if (!Track(server))
            {
                Abort(client);
                Abort(server);
                return;
            }

            await server.ConnectAsync(_target, token).ConfigureAwait(false);
            var upstream = PumpAsync(client, server, token);
            var downstream = PumpAsync(server, client, token);
            if (!await upstream.ConfigureAwait(false) | !await downstream.ConfigureAwait(false))
            {
                Abort(client);
                Abort(server);
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Refused by the server, or the guard closed: the client sees a reset, as it would
            // from 127.0.0.1 itself.
            Abort(client);
            if (server is not null)
            {
                Abort(server);
            }
        }
        finally
        {
            Release(client);
            if (server is not null)
            {
                Release(server);
            }

            EndRelay();
        }
    }

    // Copies until the source ends its side, then ends the same side of the destination. Returns
    // false when the stream failed, so both connections must be reset.
    private static async Task<bool> PumpAsync(Socket source, Socket destination, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReceiveAsync(buffer.AsMemory(0, BufferSize), SocketFlags.None, token).ConfigureAwait(false);
                if (read == 0)
                {
                    destination.Shutdown(SocketShutdown.Send);
                    return true;
                }

                var sent = 0;
                while (sent < read)
                {
                    sent += await destination.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Unblock the other direction, which may wait on a peer that will never send again.
            Abort(source);
            Abort(destination);
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool Track(Socket socket)
    {
        lock (_gate)
        {
            return !_disposed && _connections.Add(socket);
        }
    }

    private void Release(Socket socket)
    {
        lock (_gate)
        {
            _connections.Remove(socket);
        }

        socket.Dispose();
    }

    private static void Abort(Socket socket)
    {
        try
        {
            // A zero linger time makes Close send a reset instead of a normal end of stream.
            socket.LingerState = new LingerOption(true, 0);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }

        socket.Dispose();
    }
}
