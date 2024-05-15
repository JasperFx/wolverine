using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;

namespace CoreTests.Transports.Tcp;

// This is only really used in the automated testing now
// to test out the wire protocol. Otherwise, this has been superseded
// by SocketListeningAgent
public class TestingListeningAgent : IDisposable, IListener
{
    private readonly IReceiver _callback;
    private readonly CancellationToken _cancellationToken;
    private readonly TcpListener _listener;
    private readonly ActionBlock<Socket> _socketHandling;
    private readonly Uri _uri;
    private Task _receivingLoop;

    public TestingListeningAgent(IReceiver callback, IPAddress ipaddr, int port, string protocol,
        CancellationToken cancellationToken)
    {
        Port = port;
        _callback = callback;
        _cancellationToken = cancellationToken;

        _listener = new TcpListener(new IPEndPoint(ipaddr, port));
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        _socketHandling = new ActionBlock<Socket>(async s =>
        {
            await using var stream = new NetworkStream(s, true);
            await WireProtocol.ReceiveAsync(this, NullLogger.Instance, stream, _callback);
        }, new ExecutionDataflowBlockOptions { CancellationToken = _cancellationToken });

        _uri = $"{protocol}://{ipaddr}:{port}/".ToUri();
    }

    public int Port { get; }

    public void Dispose()
    {
        _socketHandling.Complete();
        _listener.Stop();
        _listener.Server.Dispose();
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        throw new NotImplementedException();
    }

    Uri IListener.Address => _uri;

    public ValueTask StopAsync()
    {
        return ValueTask.CompletedTask;
    }

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        throw new NotImplementedException();
    }

    ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        throw new NotImplementedException();
    }

    public void Start()
    {
        _listener.Start();

        _receivingLoop = Task.Run(async () =>
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_cancellationToken).ConfigureAwait(false);
                await _socketHandling.SendAsync(socket, _cancellationToken).ConfigureAwait(false);
            }
        }, _cancellationToken);
    }
}