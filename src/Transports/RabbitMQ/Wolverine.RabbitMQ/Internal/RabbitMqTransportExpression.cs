using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

public class RabbitMqTransportExpression : BrokerExpression<RabbitMqTransport, RabbitMqQueue, RabbitMqEndpoint,
    RabbitMqListenerConfiguration, RabbitMqSubscriberConfiguration, RabbitMqTransportExpression>
{
    public RabbitMqTransportExpression(RabbitMqTransport transport, WolverineOptions options) : base(transport, options)
    {
    }

    /// <summary>
    /// Opt into making Wolverine "auto ping" new listeners by trying to send a fake Wolverine "ping" message
    /// This *might* assist in Wolverine auto-starting rabbit mq connections that have failed on the Rabbit MQ side
    /// Experimental
    /// </summary>
    public RabbitMqTransportExpression AutoPingListeners()
    {
        Transport.AutoPingListeners = true;
        return this;
    }

    /// <summary>
    /// Override the sending logic behavior for unknown or missing tenant ids when
    /// using multi-tenanted brokers / virtual hosts
    /// </summary>
    /// <param name="tenantedIdBehavior"></param>
    /// <returns></returns>
    public RabbitMqTransportExpression TenantIdBehavior(TenantedIdBehavior tenantedIdBehavior)
    {
        Transport.TenantedIdBehavior = tenantedIdBehavior;
        return this;
    }

    /// <summary>
    /// Add a separate Rabbit MQ connection for a specific tenant to a separate virtual
    /// host
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="virtualHostName"></param>
    /// <returns></returns>
    public RabbitMqTransportExpression AddTenant(string tenantId, string virtualHostName)
    {
        Transport.Tenants[tenantId] = new RabbitMqTenant(tenantId, virtualHostName);
        return this;
    }

    public RabbitMqTransportExpression AddTenant(string tenantId, Uri connectionUri)
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(f => f.Uri = connectionUri);
        Transport.Tenants[tenantId] = new RabbitMqTenant(tenantId, transport);
        return this;
    }

    public RabbitMqTransportExpression AddTenant(string tenantId, Action<ConnectionFactory> configure)
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(configure);
        Transport.Tenants[tenantId] = new RabbitMqTenant(tenantId, transport);
        return this;
    }

    /// <summary>
    /// Make any necessary customizations to the Rabbit MQ client's ConnectionFactory
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public RabbitMqTransportExpression ConfigureConnection(Action<ConnectionFactory> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        configure(Transport.ConnectionFactory);

        return this;
    }

    /// <summary>
    /// Make any necessary customizations to the Rabbit MQ client's CreateChannelOptions
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public RabbitMqTransportExpression ConfigureChannelCreation(Action<WolverineRabbitMqChannelOptions> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        Transport.ChannelCreationOptions += configure;

        return this;
    }

    protected override RabbitMqListenerConfiguration createListenerExpression(RabbitMqQueue listenerEndpoint)
    {
        return new RabbitMqListenerConfiguration(listenerEndpoint, Transport);
    }

    protected override RabbitMqSubscriberConfiguration createSubscriberExpression(RabbitMqEndpoint subscriberEndpoint)
    {
        return new RabbitMqSubscriberConfiguration(subscriberEndpoint);
    }

    public BindingExpression BindExchange(string exchangeName, Action<IRabbitMqExchange>? configure = null)
    {
        DeclareExchange(exchangeName, configure);
        return new BindingExpression(exchangeName, this);
    }

    /// <summary>
    ///     Opt into using conventional Rabbit MQ routing based on the message types
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public RabbitMqTransportExpression UseConventionalRouting(
        Action<RabbitMqMessageRoutingConvention>? configure = null)
    {
        var convention = new RabbitMqMessageRoutingConvention();
        configure?.Invoke(convention);
        Options.RoutingConventions.Add(convention);

        return this;
    }

    /// <summary>
    ///     Declare a new exchange without impacting message routing or listening. The default exchange type is "fan out". This
    ///     does not respect identifier prefixes!
    /// </summary>
    /// <param name="exchangeName"></param>
    /// <param name="configure"></param>
    public RabbitMqTransportExpression DeclareExchange(string exchangeName,
        Action<IRabbitMqBindableExchange>? configure = null)
    {
        var config = new RabbitMqExchangeConfigurationExpression(exchangeName, Transport);
        configure?.Invoke(config);

        return this;
    }

    /// <summary>
    ///     Declare a binding from a Rabbit Mq exchange to a Rabbit MQ queue. This does not respect identifier prefixes!
    /// </summary>
    /// <param name="exchangeName"></param>
    /// <returns></returns>
    public BindingExpression BindExchange(string exchangeName, ExchangeType exchangeType)
    {
        return BindExchange(exchangeName, e => e.ExchangeType = exchangeType);
    }

    /// <summary>
    ///     Declare that a queue should be created with the supplied name and optional configuration. . This does not respect
    ///     identifier prefixes!
    /// </summary>
    /// <param name="queueName"></param>
    /// <param name="configure"></param>
    public RabbitMqTransportExpression DeclareQueue(string queueName, Action<IRabbitMqQueue>? configure = null)
    {
        var queue = Transport.Queues[queueName];
        configure?.Invoke(queue);

        return this;
    }

    /// <summary>
    /// Disable Wolverine's automatic dead letter queue setup, so it will not override
    /// dead letter queue exchange usage per queue or try to create Rabbit Mq objects for
    /// dead letter queueing
    /// </summary>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public RabbitMqTransportExpression DisableDeadLetterQueueing()
    {
        Transport.DeadLetterQueue.Mode = DeadLetterQueueMode.WolverineStorage;
        Transport.Exchanges.Remove(Transport.DeadLetterQueue.ExchangeName);

        return this;
    }

    /// <summary>
    /// Disable Wolverine's automatic Request/Reply queue declaration for a specific node
    /// </summary>
    /// <returns></returns>
    public RabbitMqTransportExpression DisableSystemRequestReplyQueueDeclaration()
    {
        Transport.DeclareRequestReplySystemQueue = false;

        return this;
    }

    /// <summary>
    /// Turn on listener connection only in case if you only need to listen for messages
    /// The sender connection won't be activated in this case
    /// </summary>
    /// <returns></returns>
    public RabbitMqTransportExpression UseListenerConnectionOnly()
    {
        Transport.UseListenerConnectionOnly = true;
        Transport.UseSenderConnectionOnly = false;

        return this;
    }

    /// <summary>
    /// Turn on sender connection only in case if you only need to send messages
    /// The listener connection won't be created in this case
    /// </summary>
    /// <returns></returns>
    public RabbitMqTransportExpression UseSenderConnectionOnly()
    {
        Transport.UseSenderConnectionOnly = true;
        Transport.UseListenerConnectionOnly = false;

        return this;
    }

    /// <summary>
    /// Direct Rabbit MQ queues as the control queues between Wolverine nodes
    /// This is more efficient than the built in Wolverine database control
    /// queues if Rabbit MQ is an option
    /// </summary>
    /// <returns></returns>
    public RabbitMqTransportExpression EnableWolverineControlQueues()
    {
        var queueName = "wolverine.control." + Options.Durability.AssignedNodeNumber;
        var queue = new RabbitMqQueue(queueName, Transport, EndpointRole.System)
        {
            AutoDelete = true,
            IsDurable = false,
            IsListener = true,
            IsUsedForReplies = true,
            ListenerCount = 5,
            EndpointName = "Control",
            QueueType = QueueType.classic
        };

        Transport.Queues[queueName] = queue;

        Options.Transports.NodeControlEndpoint = queue;

        return this;
    }

    public class BindingExpression
    {
        private readonly string _exchangeName;
        private readonly RabbitMqTransportExpression _parent;

        internal BindingExpression(string exchangeName, RabbitMqTransportExpression parent)
        {
            _exchangeName = exchangeName;
            _parent = parent;
        }

        /// <summary>
        ///     Bind the named exchange to a queue. The routing key will be
        ///     [exchange name]_[queue name]
        /// </summary>
        /// <param name="queueName"></param>
        /// <param name="configure">Optional configuration of the Rabbit MQ queue</param>
        public RabbitMqTransportExpression ToQueue(string queueName, Action<IRabbitMqQueue>? configure = null)
        {
            ToQueue(queueName, $"{_exchangeName}_{queueName}", configure);

            return _parent;
        }

        /// <summary>
        ///     Bind the named exchange to a queue with a user supplied binding key
        /// </summary>
        /// <param name="queueName"></param>
        /// <param name="bindingKey"></param>
        /// <param name="configure">Optional configuration of the Rabbit MQ queue</param>
        /// <param name="arguments">Optional configuration for arguments to the Rabbit MQ binding</param>
        public RabbitMqTransportExpression ToQueue(string queueName, string bindingKey,
            Action<IRabbitMqQueue>? configure = null, Dictionary<string, object>? arguments = null)
        {
            _parent.DeclareQueue(queueName, configure);
            var queue = _parent.Transport.Queues[queueName];
            var exchange = _parent.Transport.Exchanges[_exchangeName];
            if (exchange.ExchangeType is ExchangeType.Direct)
                exchange.DirectRoutingKey = bindingKey;
            queue.BindExchange(exchange.Name, bindingKey, arguments);

            return _parent;
        }
    }

    /// <summary>
    /// Customize the dead letter queueing to override the exchange or queue setup
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public RabbitMqTransportExpression CustomizeDeadLetterQueueing(DeadLetterQueue dlq)
    {
        // copying because this is a fallback to other queues by reference
        Transport.DeadLetterQueue.Mode = dlq.Mode;
        Transport.DeadLetterQueue.QueueName = dlq.QueueName;
        Transport.DeadLetterQueue.ExchangeName = dlq.ExchangeName;
        Transport.DeadLetterQueue.BindingName = dlq.BindingName;
        Transport.DeadLetterQueue.ConfigureQueue = dlq.ConfigureQueue;
        Transport.DeadLetterQueue.ConfigureExchange = dlq.ConfigureExchange;

        return this;
    }

    /// <summary>
    /// All application Rabbit MQ queues declared by this application should be quorum queues unless explicitly
    /// overridden. Wolverine's internal queues will still be "classic"
    /// </summary>
    /// <returns></returns>
    public RabbitMqTransportExpression UseQuorumQueues()
    {
        Options.Policies.Add(new LambdaEndpointPolicy<RabbitMqQueue>((queue, _) =>
        {
            if (queue.Role == EndpointRole.Application)
            {
                queue.QueueType = QueueType.quorum;
            }
        }));
        return this;
    }
    
    /// <summary>
    /// All Rabbit MQ queues declared by this application should be streams unless explicitly
    /// overridden. Wolverine's internal queues will still be "classic"
    /// </summary>
    /// <returns></returns>
    public RabbitMqTransportExpression UseStreamsAsQueues()
    {
        Options.Policies.Add(new LambdaEndpointPolicy<RabbitMqQueue>((queue, _) =>
        {
            if (queue.Role == EndpointRole.Application)
            {
                queue.QueueType = QueueType.stream;
            }
        }));
        return this;
    }
}