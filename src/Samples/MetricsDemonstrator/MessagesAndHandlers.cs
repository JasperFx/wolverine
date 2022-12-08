using System.Net;
using System.Net.Sockets;
using Wolverine;

namespace MetricsDemonstrator;

public class Message1
{
}

public class Message2
{
}

public class Message3
{
}

public class Message4
{
}

public class Message5
{
}

public class PortFinder
{
    private static readonly IPEndPoint DefaultLoopbackEndpoint = new(IPAddress.Loopback, 0);

    public static int GetAvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(DefaultLoopbackEndpoint);
        var port = ((IPEndPoint)socket.LocalEndPoint).Port;
        return port;
    }
}

public class PublishingHostedService : IHostedService
{
    private readonly List<IDisposable> _disposables = new();
    private readonly IMessageBus _bus;

    public PublishingHostedService(IMessageBus bus)
    {
        _bus = bus;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _disposables.Add(new Publisher<Message1>(_bus));
        _disposables.Add(new Publisher<Message2>(_bus));
        _disposables.Add(new Publisher<Message3>(_bus));
        _disposables.Add(new Publisher<Message4>(_bus));
        _disposables.Add(new Publisher<Message5>(_bus));
        _disposables.Add(new Publisher<Message1>(_bus));
        _disposables.Add(new Publisher<Message2>(_bus));
        _disposables.Add(new Publisher<Message3>(_bus));
        _disposables.Add(new Publisher<Message4>(_bus));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var disposable in _disposables) disposable.Dispose();

        return Task.CompletedTask;
    }
}

public class Publisher<T> : IDisposable where T : new()
{
    private static readonly Random _random = new();

    private readonly CancellationTokenSource _cancellation;
    private readonly Task _task;

    public Publisher(IMessageBus bus)
    {
        _cancellation = new CancellationTokenSource();

        _task = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                for (var i = 0; i < _random.Next(5, 20); i++)
                {
                    await bus.PublishAsync(new T());
                }

                await Task.Delay(_random.Next(10, 250));
            }
        }, _cancellation.Token);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _task.Dispose();
    }
}

public class MessageHandler
{
    private static readonly Random _random = new();

    public async Task Handle(Message1 message)
    {
        await Task.Delay(_random.Next(50, 200));

        if (_random.Next(0, 10) >= 9)
        {
            throw new DivideByZeroException();
        }
    }

    public async Task Handle(Message2 message)
    {
        await Task.Delay(_random.Next(50, 200));

        if (_random.Next(0, 10) >= 9)
        {
            throw new DivideByZeroException();
        }
    }

    public async Task Handle(Message3 message)
    {
        await Task.Delay(_random.Next(50, 200));

        if (_random.Next(0, 10) >= 9)
        {
            throw new DivideByZeroException();
        }
    }

    public async Task Handle(Message4 message)
    {
        await Task.Delay(_random.Next(50, 200));

        if (_random.Next(0, 10) >= 9)
        {
            throw new DivideByZeroException();
        }
    }

    public async Task Handle(Message5 message)
    {
        await Task.Delay(_random.Next(50, 200));

        if (_random.Next(0, 10) >= 9)
        {
            throw new DivideByZeroException();
        }
    }
}