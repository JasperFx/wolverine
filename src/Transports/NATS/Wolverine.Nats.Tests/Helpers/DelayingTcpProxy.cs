using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Wolverine.Nats.Tests.Helpers;

/// <summary>
/// A TCP proxy in front of a NATS server that holds every byte a client sends for a fixed delay before passing it
/// on, in order, while the server's bytes go back to the client at once. It stands in for a busy client or a busy
/// server: whatever a client writes reaches the server late, so anything that treats "written" as "seen by the
/// server" shows up as a lost message instead of a rare race.
/// </summary>
public sealed class DelayingTcpProxy : IAsyncDisposable
{
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly TimeSpan _clientToServerDelay;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Task> _pumps = new();
    private readonly List<TcpClient> _clients = new();
    private Task? _acceptLoop;

    public DelayingTcpProxy(string upstreamNatsUrl, TimeSpan clientToServerDelay)
    {
        var upstream = new Uri(upstreamNatsUrl);
        _upstreamHost = upstream.Host;
        _upstreamPort = upstream.Port;
        _clientToServerDelay = clientToServerDelay;
    }

    public string Url => $"nats://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(acceptAsync);
    }

    private async Task acceptAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                var server = new TcpClient { NoDelay = true };
                await server.ConnectAsync(_upstreamHost, _upstreamPort, _cancellation.Token);
                client.NoDelay = true;

                lock (_clients)
                {
                    _clients.Add(client);
                    _clients.Add(server);
                    _pumps.Add(pumpDelayedAsync(client.GetStream(), server.GetStream()));
                    _pumps.Add(pumpAsync(server.GetStream(), client.GetStream()));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task pumpAsync(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, _cancellation.Token)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), _cancellation.Token);
                await to.FlushAsync(_cancellation.Token);
            }
        }
        catch (Exception)
        {
            // A closed socket on either side ends the pump
        }
    }

    private async Task pumpDelayedAsync(Stream from, Stream to)
    {
        var chunks = Channel.CreateUnbounded<(DateTimeOffset due, byte[] data)>();

        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var (due, data) in chunks.Reader.ReadAllAsync(_cancellation.Token))
                {
                    var wait = due - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, _cancellation.Token);
                    }

                    await to.WriteAsync(data, _cancellation.Token);
                    await to.FlushAsync(_cancellation.Token);
                }
            }
            catch (Exception)
            {
                // A closed socket on either side ends the pump
            }
        });

        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, _cancellation.Token)) > 0)
            {
                await chunks.Writer.WriteAsync((DateTimeOffset.UtcNow + _clientToServerDelay, buffer[..read]),
                    _cancellation.Token);
            }
        }
        catch (Exception)
        {
            // A closed socket on either side ends the pump
        }

        chunks.Writer.TryComplete();
        await writer;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _listener.Stop();

        lock (_clients)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }

        var running = new List<Task>();
        if (_acceptLoop != null)
        {
            running.Add(_acceptLoop);
        }

        lock (_clients)
        {
            running.AddRange(_pumps);
        }

        await Task.WhenAll(running);
        _cancellation.Dispose();
    }
}
