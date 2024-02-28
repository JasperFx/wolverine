using System;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal;

internal abstract class RabbitMqConnectionAgent : IDisposable
{
    private readonly IConnection _connection;
    protected readonly object Locker = new();

    protected RabbitMqConnectionAgent(IConnection connection,
        ILogger logger)
    {
        _connection = connection;
        Logger = logger;
    }

    public ILogger Logger { get; }

    internal AgentState State { get; private set; } = AgentState.Disconnected;

    internal IModel? Channel { get; set; }

    public virtual void Dispose()
    {
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

            startNewChannel();

            State = AgentState.Connected;
        }
    }

    protected void startNewChannel()
    {
        Channel = _connection.CreateModel();

        Channel.CallbackException += (sender, args) =>
        {
            Logger.LogError(args.Exception, "Callback error in Rabbit Mq agent");
        };
        
        Channel.ModelShutdown += ChannelOnModelShutdown;
    }

    private void ChannelOnModelShutdown(object? sender, ShutdownEventArgs e)
    {
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