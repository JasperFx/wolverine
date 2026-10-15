using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-5015. A loopback TCP proxy in front of a database server that loses a reply on purpose. Once armed
/// with a trigger, the first client request the trigger matches is forwarded to the server and every byte the
/// server sends back on that connection afterwards is dropped: the server executes (and commits) what it was
/// asked to, and the client never hears about it, the way a disk stall or a dead network path looks from the
/// client. Optionally the database then stays unreachable through the proxy until <see cref="Restore"/>.
/// </summary>
public sealed class ReplyWithholdingProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<TcpClient> _sockets = new();
    private Func<byte[], bool>? _trigger;
    private bool _unreachableAfterwards;
    private TimeSpan? _severAfter;
    private int _tripped;
    private volatile bool _unreachable;

    private ReplyWithholdingProxy(string upstreamHost, int upstreamPort)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = acceptLoopAsync();
    }

    public static ReplyWithholdingProxy Start(string upstreamHost, int upstreamPort)
    {
        return new ReplyWithholdingProxy(upstreamHost, upstreamPort);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// Arm the proxy: the first request containing <paramref name="marker" /> loses its reply
    /// </summary>
    public void WithholdReplyTo(string marker, Encoding? encoding = null, bool unreachableAfterwards = false,
        TimeSpan? severAfter = null)
    {
        var bytes = (encoding ?? Encoding.UTF8).GetBytes(marker);
        WithholdReplyTo(chunk => chunk.AsSpan().IndexOf(bytes) >= 0, unreachableAfterwards, severAfter);
    }

    /// <summary>
    /// Arm the proxy: the first request chunk <paramref name="trigger" /> matches loses its reply. With
    /// <paramref name="severAfter" />, that connection is then cut instead of left hanging, for a client with no
    /// command timeout to give up on its own.
    /// </summary>
    public void WithholdReplyTo(Func<byte[], bool> trigger, bool unreachableAfterwards = false,
        TimeSpan? severAfter = null)
    {
        _unreachableAfterwards = unreachableAfterwards;
        _severAfter = severAfter;
        _tripped = 0;
        _trigger = trigger;
    }

    public bool Tripped => _tripped == 1;

    /// <summary>
    /// Let replies through again on new connections
    /// </summary>
    public void Restore()
    {
        _trigger = null;
        _unreachable = false;
    }

    private async Task acceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = relayAsync(client);
        }
    }

    private async Task relayAsync(TcpClient client)
    {
        using var server = new TcpClient();
        lock (_sockets)
        {
            _sockets.Add(client);
            _sockets.Add(server);
        }

        try
        {
            await server.ConnectAsync(_upstreamHost, _upstreamPort, _cancellation.Token);

            // A connection opened while the database is "unreachable" never hears back from it
            var withholding = _unreachable;
            var clientStream = client.GetStream();
            var serverStream = server.GetStream();

            var toServer = pumpAsync(clientStream, serverStream, chunk =>
            {
                var trigger = _trigger;
                if (trigger != null && trigger(chunk) && Interlocked.CompareExchange(ref _tripped, 1, 0) == 0)
                {
                    withholding = true;
                    if (_unreachableAfterwards) _unreachable = true;
                    if (_severAfter is { } severAfter)
                    {
                        _ = severAsync(client, server, severAfter);
                    }
                }

                return true;
            });

            var toClient = pumpAsync(serverStream, clientStream, _ => !withholding);

            // Either side closing tears down both, so the server sees the client's connection go away
            await Task.WhenAny(toServer, toClient);
        }
        catch (Exception)
        {
            // The relay ends when either side goes away
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task severAsync(TcpClient client, TcpClient server, TimeSpan after)
    {
        await Task.Delay(after);
        client.Dispose();
        server.Dispose();
    }

    private async Task pumpAsync(NetworkStream from, NetworkStream to, Func<byte[], bool> shouldForward)
    {
        var buffer = new byte[64 * 1024];
        while (!_cancellation.IsCancellationRequested)
        {
            var read = await from.ReadAsync(buffer, _cancellation.Token);
            if (read == 0) return;

            var chunk = buffer[..read];
            if (shouldForward(chunk))
            {
                await to.WriteAsync(chunk, _cancellation.Token);
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        lock (_sockets)
        {
            foreach (var socket in _sockets) socket.Dispose();
        }
    }
}
