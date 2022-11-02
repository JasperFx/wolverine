using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using RabbitMQ.Client;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    public partial class RabbitMqTransport : BrokerTransport<RabbitMqEndpoint>, IDisposable
    {
        public const string ProtocolName = "rabbitmq";
        public const string ResponseEndpointName = "RabbitMqResponses";

        private IConnection? _listenerConnection;
        private IConnection? _sendingConnection;

        public RabbitMqTransport() : base(ProtocolName, "Rabbit MQ")
        {
            ConnectionFactory.AutomaticRecoveryEnabled = true;
            Queues = new(name => new RabbitMqQueue(name, this));

            Exchanges = new(name => new RabbitMqExchange(name, this));

            Topics = new(uri =>
            {
                if (uri.Host != RabbitMqEndpoint.TopicSegment) throw new ArgumentOutOfRangeException(nameof(uri));
                var exchangeName = uri.Segments[1].TrimEnd('/');
                var exchange = Exchanges[exchangeName];
                exchange.ExchangeType = ExchangeType.Topic;
                return new RabbitMqTopicEndpoint(uri.Segments.Last(), exchange, this);
            });
        }

        public override ValueTask ConnectAsync(IWolverineRuntime runtime)
        {
            Callback = new RabbitMqChannelCallback(runtime.Logger, runtime.Advanced.Cancellation);
            
            // TODO -- log the connection
            _listenerConnection ??= BuildConnection();
            _sendingConnection ??= BuildConnection();
            
            return ValueTask.CompletedTask;
        }

        internal RabbitMqChannelCallback Callback { get; private set; }

        internal IConnection ListeningConnection => _listenerConnection ??= BuildConnection();
        internal IConnection SendingConnection => _sendingConnection ??= BuildConnection();

        public ConnectionFactory ConnectionFactory { get; } = new();

        public IList<AmqpTcpEndpoint> AmqpTcpEndpoints { get; } = new List<AmqpTcpEndpoint>();

        public LightweightCache<Uri, RabbitMqTopicEndpoint> Topics { get; }
        public LightweightCache<string, RabbitMqExchange> Exchanges { get; }

        public LightweightCache<string, RabbitMqQueue> Queues { get; }

        public void Dispose()
        {
            _listenerConnection?.Close();
            _listenerConnection?.SafeDispose();

            _sendingConnection?.Close();
            _sendingConnection?.SafeDispose();
            
            Callback?.SafeDispose();
        }

        protected override IEnumerable<RabbitMqEndpoint> endpoints()
        {
            foreach (var exchange in Exchanges)
            {
                yield return exchange;

                foreach (var topic in exchange.Topics)
                {
                    yield return topic;
                }
            }

            foreach (var queue in Queues)
            {
                yield return queue;
            }
        }

        protected override RabbitMqEndpoint findEndpointByUri(Uri uri)
        {
            var type = uri.Host;

            var name = uri.Segments.Last();
            switch (type)
            {
                case RabbitMqEndpoint.QueueSegment:
                    return Queues[name];
                
                case RabbitMqEndpoint.ExchangeSegment:
                    return Exchanges[name];
                
                case RabbitMqEndpoint.TopicSegment:
                    return Topics[uri];
                
                default:
                    throw new ArgumentOutOfRangeException(nameof(uri), $"Invalid Rabbit MQ object type '{type}'");
            }
        }

        protected override void tryBuildResponseQueueEndpoint(IWolverineRuntime runtime)
        {
            var queueName = $"wolverine.response.{runtime.Advanced.UniqueNodeId}";

            var queue = new RabbitMqQueue(queueName, this, EndpointRole.System)
            {
                AutoDelete = true,
                IsDurable = false,
                IsListener = true,
                IsUsedForReplies = true,
                ListenerCount = 5,
                EndpointName = ResponseEndpointName
            };

            Queues[queueName] = queue;
        }

        internal IConnection BuildConnection()
        {
            return AmqpTcpEndpoints.Any()
                ? ConnectionFactory.CreateConnection(AmqpTcpEndpoints)
                : ConnectionFactory.CreateConnection();
        }

        public RabbitMqQueue EndpointForQueue(string queueName)
        {
            return Queues[queueName];
        }

        public RabbitMqExchange EndpointForExchange(string exchangeName)
        {
            return Exchanges[exchangeName];
        }

        public IEnumerable<RabbitMqBinding> Bindings()
        {
            return Exchanges.SelectMany(x => x.Bindings());
        }

        public override IEnumerable<PropertyColumn> DiagnosticColumns()
        {
            yield return new PropertyColumn("Queue Name", "name");
            yield return new PropertyColumn("Message Count", "count", Justify.Right);
        }
    }
}
