using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace CoreTests;

public abstract class SendingContext : IAsyncDisposable
{
    private readonly int _senderPort;
    private IHost _receiver = null!;
    private IHost _sender = null!;

    public SendingContext()
    {
        _senderPort = PortFinder.GetAvailablePort();
        ReceiverPort = PortFinder.GetAvailablePort();
    }

    public int ReceiverPort { get; }

    internal async Task<IWolverineRuntime> theSendingRuntime() => (await theSender()).Services.GetRequiredService<IWolverineRuntime>();

    internal async Task<IHost> theSender()
    {
        if (_sender == null)
        {
            _sender = await WolverineHost.ForAsync(opts =>
            {
                opts.PublishAllMessages().ToPort(ReceiverPort);
                opts.ListenAtPort(_senderPort);
            });
        }

        return _sender;
    }

    internal async Task<IHost> theReceiver()
    {
        if (_receiver == null)
        {
            _receiver = await WolverineHost.ForAsync(opts => opts.ListenAtPort(ReceiverPort));
        }

        return _receiver;
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

    internal async Task SenderOptions(Action<WolverineOptions> configure)
    {
        _sender = await WolverineHost.ForAsync(opts =>
        {
            configure(opts);
            opts.ListenAtPort(_senderPort);
        });
    }
}