using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

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
    internal bool IsConnected => State == AgentState.Connected;

    internal IChannel? Channel { get; set; }

    public virtual async ValueTask DisposeAsync()
    {
        _monitor.Remove(this);
        await teardownChannel();
    }

    internal async Task EnsureInitiated()
    {
        if (Channel is not null)
        {
            return;
        }

        await Locker.WaitAsync();
        
        if (Channel is not null)
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

        Channel.CallbackExceptionAsync += (sender, args) =>
        {
            Logger.LogError(args.Exception, "Callback error in Rabbit Mq agent. Attempting to restart the channel");

            // Try to restart the connection
#pragma warning disable VSTHRD110
            Task.Run(async () =>
#pragma warning restore VSTHRD110
            {
                await Locker.WaitAsync();
                try
                {
                    _monitor.Remove(this);
                    try
                    {
                        await teardownChannel();
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Error when trying to tear down a blocked channel");
                    }

                    Channel = null;
                    await EnsureInitiated();
                    Logger.LogInformation("Restarted the Rabbit MQ channel");
                }
                finally
                {
                    Locker.Release();
                }
            });
            
            return Task.CompletedTask;
        };

        Channel.ChannelShutdownAsync += ChannelOnModelShutdown;

        Logger.LogInformation("Opened a new channel for Wolverine endpoint {Endpoint}", this);
    }

    private Task ChannelOnModelShutdown(object? sender, ShutdownEventArgs e)
    {
        State = AgentState.Disconnected;

        if (e.Initiator == ShutdownInitiator.Application) return Task.CompletedTask;

        if (e.Exception != null)
        {
            Logger.LogError(e.Exception,
                "Unexpected channel shutdown for Rabbit MQ. Wolverine will attempt to restart...");
        }

        return Task.CompletedTask;
    }

    internal virtual Task ReconnectedAsync()
    {
        State = AgentState.Connected;
        return Task.CompletedTask;
    }

    protected async Task teardownChannel()
    {
        if (Channel != null)
        {
            Channel.ChannelShutdownAsync -= ChannelOnModelShutdown;
            await Channel.CloseAsync();
            await Channel.AbortAsync();
            Channel.Dispose();
        }

        Channel = null;

        State = AgentState.Disconnected;
    }
}