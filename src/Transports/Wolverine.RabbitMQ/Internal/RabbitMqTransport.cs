using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    internal partial class RabbitMqTransport : TransportBase<RabbitMqEndpoint>, IDisposable
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
        }

        internal IConnection ListeningConnection => _listenerConnection ??= BuildConnection();
        internal IConnection SendingConnection => _sendingConnection ??= BuildConnection();

        /// <summary>
        /// Should Wolverine attempt to auto-provision all exchanges, queues, and bindings
        /// if they do not already exist?
        /// </summary>
        public bool AutoProvision { get; set; }

        public bool AutoPurgeAllQueues { get; set; }

        public ConnectionFactory ConnectionFactory { get; } = new();

        public IList<AmqpTcpEndpoint> AmqpTcpEndpoints { get; } = new List<AmqpTcpEndpoint>();

        public LightweightCache<string, RabbitMqExchange> Exchanges { get; }

        public LightweightCache<string, RabbitMqQueue> Queues { get; }

        public void Dispose()
        {
            _listenerConnection?.Close();
            _listenerConnection?.SafeDispose();

            _sendingConnection?.Close();
            _sendingConnection?.SafeDispose();
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
                
                default:
                    throw new ArgumentOutOfRangeException(nameof(uri), $"Invalid Rabbit MQ object type '{type}'");
            }
        }

        public override ValueTask InitializeAsync(IWolverineRuntime runtime)
        {
            tryBuildResponseQueueEndpoint(runtime);
            
            InitializeAllObjects(runtime.Logger);

            return ValueTask.CompletedTask;
        }

        private void tryBuildResponseQueueEndpoint(IWolverineRuntime runtime)
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
    }

    public interface IBindingExpression
    {
        /// <summary>
        ///     Bind the named exchange to a queue. The routing key will be
        ///     [exchange name]_[queue name]
        /// </summary>
        /// <param name="queueName"></param>
        /// <param name="configure">Optional configuration of the Rabbit MQ queue</param>
        /// <param name="arguments">Optional configuration for arguments to the Rabbit MQ binding</param>
        IRabbitMqTransportExpression ToQueue(string queueName, Action<RabbitMqQueue>? configure = null,
            Dictionary<string, object>? arguments = null);

        /// <summary>
        ///     Bind the named exchange to a queue with a user supplied binding key
        /// </summary>
        /// <param name="queueName"></param>
        /// <param name="bindingKey"></param>
        /// <param name="configure">Optional configuration of the Rabbit MQ queue</param>
        /// <param name="arguments">Optional configuration for arguments to the Rabbit MQ binding</param>
        IRabbitMqTransportExpression ToQueue(string queueName, string bindingKey,
            Action<RabbitMqQueue>? configure = null, Dictionary<string, object>? arguments = null);
    }
}
