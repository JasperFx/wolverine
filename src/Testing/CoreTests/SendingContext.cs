using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using Wolverine.Runtime;
using Wolverine.Transports.Tcp;

namespace CoreTests;

public abstract class SendingContext : IAsyncDisposable
{
    private readonly int _senderPort;
    private IHost _receiver;
    private IHost _sender;

    public SendingContext()
    {
        _senderPort = PortFinder.GetAvailablePort();
        ReceiverPort = PortFinder.GetAvailablePort();
    }

    public int ReceiverPort { get; }

    internal IWolverineRuntime theSendingRuntime => theSender.Services.GetRequiredService<IWolverineRuntime>();

    internal IHost theSender
    {
        get
        {
            if (_sender == null)
            {
                _sender = WolverineHost.For(opts =>
                {
                    opts.PublishAllMessages().ToPort(ReceiverPort);
                    opts.ListenAtPort(_senderPort);
                });
            }

            return _sender;
        }
    }

    internal IHost theReceiver
    {
        get
        {
            if (_receiver == null)
            {
                _receiver = WolverineHost.For(opts => opts.ListenAtPort(ReceiverPort));
            }

            return _receiver;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_receiver != null)
        {
            await _receiver.StopAsync();
            _receiver.Dispose();
        }

        if (_sender != null)
        {
            await _sender.StopAsync();
            _sender.Dispose();
        }
    }

    internal void SenderOptions(Action<WolverineOptions> configure)
    {
        _sender = WolverineHost.For(opts =>
        {
            configure(opts);
            opts.ListenAtPort(_senderPort);
        });
    }
}