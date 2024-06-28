using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal;

/// <summary>
/// Base class for Rabbit MQ listeners and senders
/// </summary>
internal abstract class RabbitMqChannelAgent : IAsyncDisposable
{
    private readonly ConnectionMonitor _monitor;
    protected readonly SemaphoreSlim Locker = new(1, 1);

    protected RabbitMqChannelAgent(ConnectionMonitor monitor,
        ILogger logger)
    {
        _monitor = monitor;
        Logger = logger;
        monitor.Track(this);
    }

    public ILogger Logger { get; }

    internal AgentState State { get; private set; } = AgentState.Disconnected;

    internal IChannel? Channel { get; set; }

    public virtual async ValueTask DisposeAsync()
    {
        _monitor.Remove(this);
        await teardownChannel();
    }

    internal async Task EnsureConnected()
    {
        if (State == AgentState.Connected)
        {
            return;
        }

        await Locker.WaitAsync();
        
        if (State == AgentState.Connected)
        {
            return;
        }

        try
        {
            await startNewChannel();
            State = AgentState.Connected;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Error trying to start a new Rabbit MQ channel for {Endpoint}", this);
        }
        finally
        {
            Locker.Release();
        }
    }

    protected async Task startNewChannel()
    {
        Channel = await _monitor.CreateChannelAsync();

        Channel.CallbackException += (sender, args) =>
        {
            Logger.LogError(args.Exception, "Callback error in Rabbit Mq agent");
        };

        Channel.ChannelShutdown += ChannelOnModelShutdown;

        Logger.LogInformation("Opened a new channel for Wolverine endpoint {Endpoint}", this);
    }

    private void ChannelOnModelShutdown(object? sender, ShutdownEventArgs e)
    {
        if (e.Initiator == ShutdownInitiator.Application) return;

        if (e.Exception != null)
        {
            Logger.LogError(e.Exception,
                "Unexpected channel shutdown for Rabbit MQ. Wolverine will attempt to restart...");
        }

        _ = EnsureConnected();
    }

    protected async Task teardownChannel()
    {
        if (Channel != null)
        {
            Channel.ChannelShutdown -= ChannelOnModelShutdown;
            await Channel.CloseAsync();
            await Channel.AbortAsync();
            Channel.Dispose();
        }

        Channel = null;

        State = AgentState.Disconnected;
    }
}