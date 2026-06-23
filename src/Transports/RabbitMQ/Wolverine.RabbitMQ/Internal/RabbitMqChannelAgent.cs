using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

/// <summary>
/// Base class for Rabbit MQ listeners and senders
/// </summary>
internal abstract class RabbitMqChannelAgent : IAsyncDisposable, IReportConnectionState
{
    private readonly ConnectionMonitor _monitor;
    private readonly SemaphoreSlim Locker = new(1, 1);
    private bool _disposed;

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

    // Surfaces the channel agent's connection state to EndpointHealthSnapshot so external monitors can see a
    // dead-but-Accepting listener (or a disconnected sender) directly rather than inferring it from staleness.
    public TransportConnectionState ConnectionState =>
        State == AgentState.Connected ? TransportConnectionState.Connected : TransportConnectionState.Disconnected;

    internal IChannel? Channel { get; set; }

    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _monitor.Remove(this);
        await teardownChannel();

        // Intentionally NOT calling Locker.Dispose() — rapid pause/restart cycles
        // can have an in-flight WaitAsync/Release race with disposal, which would
        // throw ObjectDisposedException. The kernel handle is reclaimed by the
        // SemaphoreSlim finalizer. See #3132.
    }

    internal async Task EnsureInitiated()
    {
        if (_disposed)
            return;

        // A non-null but closed channel is a dead channel. Treating "channel exists"
        // as "channel healthy" would latch the agent permanently after a channel-only
        // shutdown that didn't surface as a callback exception (see #3171), so we
        // re-build whenever the channel is missing or no longer open.
        if (Channel is { IsOpen: true })
            return;

        await Locker.WaitAsync();
        try
        {
            if (_disposed)
                return;

            if (Channel is { IsOpen: true })
                return;

            // Drop a stale, closed channel before opening a fresh one. teardownChannel()
            // unsubscribes the handlers and nulls Channel so startNewChannel() has a clean slate.
            if (Channel is not null)
            {
                try
                {
                    await teardownChannel();
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Error tearing down a stale Rabbit MQ channel for {Endpoint}", this);
                }
            }

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

        Channel.CallbackExceptionAsync += HandleChannelExceptionAsync;
        Channel.ChannelShutdownAsync += HandleChannelShutdownAsync;

        Logger.LogInformation("Opened a new channel for Wolverine endpoint {Endpoint}", this);
    }

    private Task HandleChannelExceptionAsync(object? sender, CallbackExceptionEventArgs args)
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

                // EnsureInitiated can be used here as Locker(SemaphoreSlim) is not re-entrant
                await startNewChannel();
                State = AgentState.Connected;

                Logger.LogInformation("Restarted the Rabbit MQ channel");
            }
            finally
            {
                Locker.Release();
            }
        });

        return Task.CompletedTask;
    }

    private Task HandleChannelShutdownAsync(object? sender, ShutdownEventArgs e)
    {
        State = AgentState.Disconnected;

        if (e.Initiator == ShutdownInitiator.Application) return Task.CompletedTask;

        if (e.Exception != null)
        {
            Logger.LogError(e.Exception,
                "Unexpected channel shutdown for Rabbit MQ. Wolverine will attempt to restart...");
        }
        else
        {
            Logger.LogWarning(
                "Unexpected channel shutdown for Rabbit MQ ({Endpoint}). Wolverine will attempt to restart...", this);
        }

        HandleUnexpectedChannelShutdown();

        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the underlying RabbitMQ connection is still alive. A channel-only shutdown
    /// (see #3171) leaves this true; a full connection drop sets it false and is recovered
    /// separately through <see cref="ConnectionMonitor"/>.
    /// </summary>
    protected bool ConnectionIsLive => _monitor.IsConnected;

    /// <summary>
    /// Hook for derived agents to eagerly recover from an unexpected channel-only shutdown.
    /// Senders heal lazily on the next send through <see cref="EnsureInitiated"/>; listeners sit
    /// blocked on the broker and won't self-pull, so they override this to re-declare/re-consume.
    /// </summary>
    protected virtual void HandleUnexpectedChannelShutdown()
    {
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
            Channel.ChannelShutdownAsync -= HandleChannelShutdownAsync;
            Channel.CallbackExceptionAsync -= HandleChannelExceptionAsync;
            await Channel.CloseAsync();
            await Channel.AbortAsync();
            Channel.Dispose();
        }

        Channel = null;

        State = AgentState.Disconnected;
    }
}