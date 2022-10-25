using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    public class RabbitMqExchange : RabbitMqEndpoint
    {
        private readonly RabbitMqTransport _parent;
        
        internal LightweightCache<string, RabbitMqTopicEndpoint> Topics { get; }

        internal RabbitMqExchange(string name, RabbitMqTransport parent) 
            : base(new Uri($"{RabbitMqTransport.ProtocolName}://{RabbitMqEndpoint.ExchangeSegment}/{name}"), EndpointRole.Application, parent)
        {
            _parent = parent;
            Name = name;
            DeclaredName = name == TransportConstants.Default ? "" : Name;
            ExchangeName = name;

            Topics = new(topic => new RabbitMqTopicEndpoint(topic, this, _parent));
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            throw new NotSupportedException();
        }

        private bool _initialized;
        
        public override ValueTask InitializeAsync(ILogger logger)
        {
            if (_initialized) return ValueTask.CompletedTask;
            if (_parent.AutoProvision)
            {
                using var model = _parent.ListeningConnection.CreateModel();
                Declare(model, logger);
            }

            _initialized = true;
            
            return ValueTask.CompletedTask;
        }

        internal override string RoutingKey()
        {
            return string.Empty;
        }

        public bool HasDeclared { get; private set; }

        public string Name { get; }

        public bool IsDurable { get; set; } = true;

        public string DeclaredName { get; }

        public ExchangeType ExchangeType { get; set; } = ExchangeType.Fanout;


        public bool AutoDelete { get; set; } = false;

        public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();

        internal void Declare(IModel channel, ILogger logger)
        {
            if (DeclaredName == string.Empty)
            {
                return;
            }

            if (HasDeclared) return;

            var exchangeTypeName = ExchangeType.ToString().ToLower();
            channel.ExchangeDeclare(DeclaredName, exchangeTypeName, IsDurable, AutoDelete, Arguments);
            logger.LogInformation("Declared Rabbit Mq exchange '{Name}', type = {Type}, IsDurable = {IsDurable}, AutoDelete={AutoDelete}", DeclaredName, exchangeTypeName, IsDurable, AutoDelete);

            foreach (var binding in _bindings.Values)
            {
                binding.Queue.Declare(channel, logger);
                binding.Declare(channel, logger);
            }

            HasDeclared = true;
        }


        public void Teardown(IModel channel)
        {
            if (DeclaredName == string.Empty)
            {
                return;
            }

            foreach (var binding in _bindings.Values)
            {
                binding.Teardown(channel);
            }

            channel.ExchangeDelete(DeclaredName);
        }

        private readonly Dictionary<string, RabbitMqBinding> _bindings = new();

        public IEnumerable<RabbitMqBinding> Bindings()
        {
            return _bindings.Values;
        }

        public RabbitMqBinding BindQueue(string queueName, string? bindingKey = null)
        {
            if (queueName == null)
            {
                throw new ArgumentNullException(nameof(queueName));
            }

            if (_bindings.TryGetValue(queueName, out var binding)) return binding;

            var queue = _parent.Queues[queueName];

            binding = new RabbitMqBinding(Name, queue, bindingKey);
            _bindings[queueName] = binding;

            return binding;
        }

        /// <summary>
        /// Declare a Rabbit MQ binding with the supplied topic pattern to
        /// the queue
        /// </summary>
        /// <param name="topicPattern"></param>
        /// <param name="bindingName"></param>
        /// <exception cref="NotImplementedException"></exception>
        public TopicBinding BindTopic(string topicPattern)
        {
            return new TopicBinding(this, topicPattern);
        }

        public class TopicBinding
        {
            private readonly RabbitMqExchange _exchange;
            private readonly string _topicPattern;

            public TopicBinding(RabbitMqExchange exchange, string topicPattern)
            {
                _exchange = exchange;
                _topicPattern = topicPattern;
            }

            /// <summary>
            /// Create a binding of the topic pattern previously specified to a Rabbit Mq queue
            /// </summary>
            /// <param name="queueName">The name of the Rabbit Mq queue</param>
            /// <param name="configureQueue">Optionally configure </param>
            public void ToQueue(string queueName, Action<RabbitMqQueue>? configureQueue = null)
            {
                var binding = _exchange.BindQueue(queueName, _topicPattern);
                configureQueue?.Invoke(binding.Queue);
            }
        }
    }
}
