using System;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal
{
    internal abstract class RabbitMqConnectionAgent : IDisposable
    {
        private readonly IConnection _connection;
        private readonly RabbitMqEndpoint _queue;
        protected readonly ILogger _logger;
        protected readonly object Locker = new();

        protected RabbitMqConnectionAgent(IConnection connection,
            RabbitMqEndpoint queue, ILogger logger)
        {
            _connection = connection;
            _queue = queue;
            _logger = logger;
        }

        internal AgentState State { get; private set; } = AgentState.Disconnected;

        internal IModel Channel { get; set; }

        public virtual void Dispose()
        {
            teardownChannel();
        }

        internal void EnsureConnected()
        {
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
}
