using System.Net;
using System.Net.Sockets;
using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Transports.Tcp;

public class SocketListener : IListener, IDisposable
{
    private readonly IPAddress _ipaddr;
    private readonly ILogger _logger;
    private readonly CancellationToken _parentToken;
    private readonly int _port;
    private readonly IReceiver _receiver;
    private CancellationToken _cancellationToken;
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _receivingLoop;
    private Block<Socket>? _socketHandling;

    public SocketListener(TcpEndpoint endpoint, IReceiver receiver, ILogger logger, IPAddress ipaddr, int port,
        CancellationToken cancellationToken)
    {
        _logger = logger;
        _port = port;
        _ipaddr = ipaddr;
        _parentToken = cancellationToken;

        Address = endpoint.Uri;

        _receiver = receiver;

        startListening(receiver);
    }

    public void Dispose()
    {
        _socketHandling?.Complete();
        _listener?.Stop();
        _listener?.Server.Dispose();
        _receivingLoop?.Dispose();
        _receiver.Dispose();
    }

    public Task<bool> TryRequeueAsync(Envelope envelope)
    {
        return Task.FromResult(false);
    }

    public Uri Address { get; }

    public async ValueTask DisposeAsync()
    {
        _listenerCancellation?.Cancel();
        _listener?.Stop();
        _listener = null;

        if (_receivingLoop != null)
        {
            await _receivingLoop;
            _receivingLoop.Dispose();
            _receivingLoop = null;
        }

        if (_socketHandling != null)
        {
            _socketHandling.Complete();
            _socketHandling = null;
        }
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync()
    {
        return DisposeAsync();
    }

    private void startListening(IReceiver callback)
    {
        _listenerCancellation = new CancellationTokenSource();
        _cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_parentToken, _listenerCancellation.Token)
            .Token;

        _listener = new TcpListener(new IPEndPoint(_ipaddr, _port));
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        _socketHandling = new Block<Socket>(async (s, _) =>
        {
            await using var stream = new NetworkStream(s, true);
            await HandleStreamAsync(callback, stream);
        });

        _receivingLoop = Task.Run(async () =>
        {
            _listener.Start();

            while (!_cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var socket = await _listener.AcceptSocketAsync(_cancellationToken);
                    await _socketHandling.PostAsync(socket);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, _cancellationToken);
    }

    public Task HandleStreamAsync(IReceiver receiver, Stream stream)
    {
        return WireProtocol.ReceiveAsync(this, _logger, stream, receiver);
    }
}