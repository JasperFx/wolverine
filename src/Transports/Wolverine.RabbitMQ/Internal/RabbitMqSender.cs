using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal
{
    public class RabbitMqSender : RabbitMqConnectionAgent, ISender
    {
        private readonly RabbitMqEndpoint _endpoint;
        private readonly string _exchangeName;
        private readonly bool _isDurable;
        private readonly string _key;
        private Func<Envelope, string> _toRoutingKey;

        public RabbitMqSender(RabbitMqEndpoint endpoint, RabbitMqTransport transport,
            RoutingMode routingType, ILogger logger) : base(
            transport.SendingConnection, transport, endpoint, logger)
        {
            _endpoint = endpoint;
            Destination = endpoint.Uri;

            _isDurable = endpoint.Mode == EndpointMode.Durable;

            _exchangeName = endpoint.ExchangeName == TransportConstants.Default ? "" : endpoint.ExchangeName;
            _key = endpoint.RoutingKey ?? endpoint.QueueName ?? "";

            _toRoutingKey = routingType == RoutingMode.Static ? _ => _key : TopicRouting.DetermineTopicName;
        }

        public bool SupportsNativeScheduledSend { get; } = false;
        public Uri Destination { get; }

        public ValueTask SendAsync(Envelope envelope)
        {
            EnsureConnected();

            if (State == AgentState.Disconnected)
            {
                throw new InvalidOperationException($"The RabbitMQ agent for {Destination} is disconnected");
            }

            var props = Channel.CreateBasicProperties();
            props.Persistent = _isDurable;
            props.Headers = new Dictionary<string, object>();

            _endpoint.MapEnvelopeToOutgoing(envelope, props);

            var routingKey = _toRoutingKey(envelope);
            Channel.BasicPublish(_exchangeName, routingKey, props, envelope.Data);

            return ValueTask.CompletedTask;
        }

        public Task<bool> PingAsync()
        {
            lock (Locker)
            {
                if (State == AgentState.Connected)
                {
                    return Task.FromResult(true);
                }

                startNewChannel();

                if (Channel.IsOpen)
                {
                    return Task.FromResult(true);
                }

                teardownChannel();
                return Task.FromResult(false);
            }
        }
    }
}
