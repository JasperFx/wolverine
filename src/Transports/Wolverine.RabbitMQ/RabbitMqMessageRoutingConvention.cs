using System;
using System.Collections.Generic;
using System.Linq;
using Baseline;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Util;

namespace Wolverine.RabbitMQ
{
    public class RabbitMappingContext
    {
        public Type MessageType { get; }
        public IWolverineRuntime Runtime { get; }

        public RabbitMappingContext(Type messageType, IWolverineRuntime runtime)
        {
            MessageType = messageType;
            Runtime = runtime;
        }
    }

    /// <summary>
    /// Conventional message routing for Rabbit MQ. By default, sends messages to an
    /// exchange named after the MessageTypeName bound to a queue of the same name.
    /// </summary>
    public class RabbitMqMessageRoutingConvention : IMessageRoutingConvention
    {
        /// <summary>
        /// Optionally include (allow list) or exclude (deny list) types. By default, this will apply to all message types
        /// </summary>
        internal CompositeFilter<Type> TypeFilters { get; } = new();

        /// <summary>
        /// Create an allow list of included message types. This is accumulative.
        /// </summary>
        /// <param name="filter"></param>
        public void IncludeTypes(Func<Type, bool> filter)
        {
            TypeFilters.Includes.Add(filter);
        }
        
        /// <summary>
        /// Create an deny list of included message types. This is accumulative.
        /// </summary>
        /// <param name="filter"></param>
        public void ExcludeTypes(Func<Type, bool> filter)
        {
            TypeFilters.Excludes.Add(filter);
        }
        
        void IMessageRoutingConvention.DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
        {
            var transport = runtime.Options.RabbitMqTransport();

            foreach (var messageType in handledMessageTypes.Where(t => TypeFilters.Matches(t)))
            {
                // Can be null, so bail out if there's no queue
                var queueName = _queueNameForListener(messageType);
                if (queueName.IsEmpty()) return;

                var endpoint = transport.EndpointForQueue(queueName);
                endpoint.Mode = Mode;
                endpoint.IsListener = true;
                var queue = transport.Queues[queueName];

                var context = new RabbitMappingContext(messageType, runtime);
                var configuration = new RabbitMqListenerConfiguration(endpoint);
                
                _configureListener(configuration, queue, context);
                
                configuration.As<IDelayedEndpointConfiguration>().Apply();
            }
        }

        IEnumerable<Endpoint> IMessageRoutingConvention.DiscoverSenders(Type messageType, IWolverineRuntime runtime)
        {
            if (!TypeFilters.Matches(messageType)) yield break;
            
            var transport = runtime.Options.RabbitMqTransport();

            // HAVE THIS BUILD THE EXCHANGE, and alternatively do all the bindings. Return the sending endpoint!
            var exchangeName = _exchangeNameForSending(messageType);
            if (exchangeName.IsEmpty()) yield break;
            var exchange = transport.Exchanges[exchangeName];

            var endpoint = transport.EndpointForExchange(exchangeName);
            endpoint.Mode = Mode;

            var configuration = new RabbitMqSubscriberConfiguration(endpoint);
            
            _configureSending(configuration, exchange, new RabbitMappingContext(messageType, runtime));

            configuration.As<IDelayedEndpointConfiguration>().Apply();
            
            // This will start up the sending agent
            var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
            yield return sendingAgent.Endpoint;
        }

        internal EndpointMode Mode { get; set; } = EndpointMode.BufferedInMemory;

        private Func<Type, string?> _queueNameForListener = t => t.ToMessageTypeName();
        private Action<RabbitMqListenerConfiguration, RabbitMqQueue, RabbitMappingContext> _configureListener = (_, _, _) => { };
        private Func<Type, string?> _exchangeNameForSending = t => t.ToMessageTypeName();

        private Action<RabbitMqSubscriberConfiguration, RabbitMqExchange, RabbitMappingContext> _configureSending = (_, exchange, _) =>
        {
            exchange.BindQueue(exchange.Name);
        };

        /// <summary>
        /// Override the convention for determining the exchange name for outgoing messages of the message type.
        /// Returning null or empty is interpreted as "don't publish this message type". Default is the MessageTypeName
        /// </summary>
        /// <param name="nameSource"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public RabbitMqMessageRoutingConvention ExchangeNameForSending(Func<Type, string?> nameSource)
        {
            _exchangeNameForSending = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
            return this;
        }

        /// <summary>
        /// Override the convention for determining the queue name for receiving incoming messages of the message type.
        /// Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the MessageTypeName
        /// </summary>
        /// <param name="nameSource"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public RabbitMqMessageRoutingConvention QueueNameForListener(Func<Type, string?> nameSource)
        {
            _queueNameForListener = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
            return this;
        }

        /// <summary>
        /// Override the Rabbit MQ and Wolverine configuration for new listening endpoints created by message type.
        /// </summary>
        /// <param name="configure"></param>
        /// <returns></returns>
        public RabbitMqMessageRoutingConvention ConfigureListeners(Action<RabbitMqListenerConfiguration, RabbitMqQueue, RabbitMappingContext> configure)
        {
            _configureListener = configure ?? throw new ArgumentNullException(nameof(configure));
            return this;
        }

        /// <summary>
        /// Override the Rabbit MQ and Wolverine configuration for sending endpoints, exchanges, and queue bindings
        /// for a new sending endpoint
        /// </summary>
        /// <param name="configure"></param>
        /// <returns></returns>
        public RabbitMqMessageRoutingConvention ConfigureSending(Action<RabbitMqSubscriberConfiguration, RabbitMqExchange, RabbitMappingContext> configure)
        {
            _configureSending = configure ?? throw new ArgumentNullException(nameof(configure));
            return this;
        }

        /// <summary>
        /// All listening and sending endpoints will be durable with persistent Inbox or Outbox
        /// mechanics
        /// </summary>
        /// <returns></returns>
        public RabbitMqMessageRoutingConvention InboxedListenersAndOutboxedSenders()
        {
            Mode = EndpointMode.Durable;
            return this;
        }

        /// <summary>
        /// All listening and sending endpoints will use the inline mode
        /// </summary>
        /// <returns></returns>
        public RabbitMqMessageRoutingConvention InlineListenersAndSenders()
        {
            Mode = EndpointMode.Inline;
            return this;
        }

        /// <summary>
        /// All listening and sending endpoints will use buffered mechanics. This is the default
        /// </summary>
        /// <returns></returns>
        public RabbitMqMessageRoutingConvention BufferedListenersAndSenders()
        {
            Mode = EndpointMode.BufferedInMemory;
            return this;
        }
    }
}
