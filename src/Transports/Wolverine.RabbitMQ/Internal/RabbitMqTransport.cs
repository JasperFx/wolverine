using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    public partial class RabbitMqTransport : TransportBase<RabbitMqEndpoint>, IDisposable
    {
        public const string ProtocolName = "rabbitmq";
        public const string ResponseEndpointName = "RabbitMqResponses";

        private readonly LightweightCache<Uri, RabbitMqEndpoint> _endpoints;
        private IConnection? _listenerConnection;
        private IConnection? _sendingConnection;

        public RabbitMqTransport() : base(ProtocolName, "Rabbit MQ")
        {
            ConnectionFactory.AutomaticRecoveryEnabled = true;

            _endpoints =
                new LightweightCache<Uri, RabbitMqEndpoint>(uri =>
                {
                    var endpoint = new RabbitMqEndpoint(this);
                    endpoint.Parse(uri);

                    return endpoint;
                });

            Exchanges = new LightweightCache<string, RabbitMqExchange>(name => new RabbitMqExchange(name, this));
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

        public LightweightCache<string, RabbitMqQueue> Queues { get; } = new(name => new RabbitMqQueue(name));

        public void Dispose()
        {
            _listenerConnection?.Close();
            _listenerConnection?.SafeDispose();

            _sendingConnection?.Close();
            _sendingConnection?.SafeDispose();
        }

        protected override IEnumerable<RabbitMqEndpoint> endpoints()
        {
            return _endpoints;
        }

        protected override RabbitMqEndpoint findEndpointByUri(Uri uri)
        {
            return _endpoints[uri];
        }

        public override ValueTask InitializeAsync(IWolverineRuntime runtime)
        {
            tryBuildResponseQueueEndpoint(runtime);
            
            if (AutoProvision)
            {
                InitializeAllObjects(runtime.Logger);
            }

            if (AutoPurgeAllQueues)
            {
                PurgeAllQueues();
            }

            return ValueTask.CompletedTask;
        }

        private void tryBuildResponseQueueEndpoint(IWolverineRuntime runtime)
        {
            var queueName = $"wolverine.response.{runtime.Advanced.UniqueNodeId}";

            var queue = Queues[queueName];
            queue.AutoDelete = true;
            queue.IsDurable = false;

            var endpoint = new RabbitMqEndpoint(EndpointRole.System,this)
            {
                QueueName = queueName,
                IsListener = true,
                Mode = EndpointMode.Inline,
                IsUsedForReplies = true,
                ListenerCount = 5,
                Name = ResponseEndpointName
            };

            _endpoints[endpoint.Uri] = endpoint;
        }

        internal IConnection BuildConnection()
        {
            return AmqpTcpEndpoints.Any()
                ? ConnectionFactory.CreateConnection(AmqpTcpEndpoints)
                : ConnectionFactory.CreateConnection();
        }

        public RabbitMqEndpoint EndpointForQueue(string queueName)
        {
            // Yeah, it's super inefficient, but it only happens once or twice
            // when bootstrapping'
            var temp = new RabbitMqEndpoint(this) { QueueName = queueName };
            return findEndpointByUri(temp.Uri);
        }

        public RabbitMqEndpoint EndpointFor(string routingKey, string exchangeName)
        {
            var temp = new RabbitMqEndpoint(this)
            {
                RoutingKey = routingKey,
                ExchangeName = exchangeName
            };

            return findEndpointByUri(temp.Uri);
        }

        public RabbitMqEndpoint EndpointForExchange(string exchangeName)
        {
            var temp = new RabbitMqEndpoint(this) { ExchangeName = exchangeName };
            return findEndpointByUri(temp.Uri);
        }

        internal void InitializeEndpoint(RabbitMqEndpoint endpoint, IModel channel, ILogger logger)
        {
            // This bit of hokey-ness is 
            if (endpoint.QueueName.IsNotEmpty() && endpoint.QueueName.StartsWith("wolverine.") &&
                endpoint.Role == EndpointRole.Application)
            {
                return;
            }
            
            if (AutoProvision)
            {
                if (endpoint.ExchangeName.IsNotEmpty())
                {
                    var exchange = Exchanges[endpoint.ExchangeName];
                    exchange.Declare(channel!, logger);
                }

                if (endpoint.QueueName.IsNotEmpty())
                {
                    var queue = Queues[endpoint.QueueName];
                    queue.Initialize(channel, logger, AutoPurgeAllQueues);
                }
            }
            else if (endpoint.QueueName.IsNotEmpty() && endpoint.QueueName.IsNotEmpty())
            {
                var queue = Queues[endpoint.QueueName];

                if (!queue.IsDurable || queue.AutoDelete) return;

                if (queue.PurgeOnStartup || AutoPurgeAllQueues)
                {
                    channel!.QueuePurge(queue.Name);
                }
            }
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
