using System;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal;

/// <summary>
/// Base class for Rabbit MQ listeners and senders
/// </summary>
internal abstract class RabbitMqChannelAgent : IDisposable
{
    private readonly ConnectionMonitor _monitor;
    protected readonly object Locker = new();

    protected RabbitMqChannelAgent(ConnectionMonitor monitor,
        ILogger logger)
    {
        _monitor = monitor;
        Logger = logger;
        monitor.Track(this);
    }

    public ILogger Logger { get; }

    internal AgentState State { get; private set; } = AgentState.Disconnected;

    internal IModel? Channel { get; set; }

    public virtual void Dispose()
    {
        _monitor.Remove(this);
        teardownChannel();
    }

    internal void EnsureConnected()
    {
        if (State == AgentState.Connected)
        {
            return;
        }

        lock (Locker)
        {
            if (State == AgentState.Connected)
            {
                return;
            }

            try
            {
                startNewChannel();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to start a new Rabbit MQ channel for {Endpoint}", this);
            }

            State = AgentState.Connected;
        }
    }

    protected void startNewChannel()
    {
        Channel = _monitor.CreateModel();

        Channel.CallbackException += (sender, args) =>
        {
            Logger.LogError(args.Exception, "Callback error in Rabbit Mq agent");
        };

        Channel.ModelShutdown += ChannelOnModelShutdown;

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

        EnsureConnected();
    }

    protected void teardownChannel()
    {
        if (Channel != null)
        {
            Channel.ModelShutdown -= ChannelOnModelShutdown;
            Channel.Close();
            Channel.Abort();
            Channel.Dispose();
        }

        Channel = null;

        State = AgentState.Disconnected;
    }
}