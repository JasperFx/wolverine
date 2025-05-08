using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Util;

namespace Wolverine.RabbitMQ;

public sealed class RabbitMqConventionalListenerConfiguration : RabbitMqListenerConfiguration
{
    private readonly Func<Type, string?> _senderConvention;

    public RabbitMqConventionalListenerConfiguration(RabbitMqQueue endpoint, RabbitMqTransport transport, Func<Type, string?> senderConvention) : base(endpoint, transport)
    {
        _senderConvention = senderConvention;
    }
    
    /// <summary>
    /// Binds to an exchange with provided name & type
    /// </summary>
    /// <param name="bindingKey"></param>
    /// <param name="arguments"></param>
    /// <returns></returns>
    public RabbitMqListenerConfiguration BindToExchange(
        ExchangeType exchangeType,
        string exchangeName, 
        string? bindingKey = null,
        Dictionary<string, object>? arguments = null)
    {
        var exchange = Transport.Exchanges[exchangeName];
        exchange.ExchangeType = exchangeType;
        if (exchangeType is ExchangeType.Direct)
            exchange.DirectRoutingKey = bindingKey;
        
        add(e=> e.BindExchange(exchangeName, exchangeType == ExchangeType.Headers ? string.Empty : bindingKey, arguments));

        return this;
    }
    
    /// <summary>
    /// Binds to an exchange named with the message type name. 
    /// </summary>
    /// <param name="bindingKey"></param>
    /// <param name="arguments"></param>
    /// <typeparam name="TMessage"></typeparam>
    /// <returns></returns>
    public RabbitMqListenerConfiguration BindToExchange<TMessage>(
        ExchangeType exchangeType,
        string? bindingKey = null,
        Dictionary<string, object>? arguments = null)
    {
        var convention = _senderConvention(typeof(TMessage))!;
        var name = Transport.MaybeCorrectName(convention);
        return BindToExchange(exchangeType, name, bindingKey ?? name, arguments);
    }
}

public class RabbitMqListenerConfiguration : ListenerConfiguration<RabbitMqListenerConfiguration, RabbitMqQueue>
{
    protected readonly RabbitMqQueue Queue;
    protected readonly RabbitMqTransport Transport;

    public RabbitMqListenerConfiguration(RabbitMqQueue endpoint, RabbitMqTransport transport) : base(endpoint)
    {
        Queue = endpoint;
        Transport = transport;
    }

    public string QueueName => Queue.QueueName;

    /// <summary>
    /// Override the queue declaration to opt into using a quorum queue
    /// or declaring as a stream. Default is a classic queue
    /// </summary>
    /// <param name="queueType"></param>
    /// <returns></returns>
    public RabbitMqListenerConfiguration QueueType(QueueType queueType)
    {
        add(e =>
        {
            e.QueueType = queueType;
        });

        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public RabbitMqListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    ///     Override the Rabbit MQ PreFetchCount value for just this endpoint for how many
    ///     messages can be pre-fetched into memory before being handled
    /// </summary>
    /// <param name="count"></param>
    /// <returns></returns>
    public RabbitMqListenerConfiguration PreFetchCount(ushort count)
    {
        add(e => e.PreFetchCount = count);
        return this;
    }

    /// <summary>
    /// Use a custom interoperability strategy to map Wolverine messages to an upstream
    /// system's protocol
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public RabbitMqListenerConfiguration UseInterop(IRabbitMqEnvelopeMapper mapper)
    {
        add(e => e.EnvelopeMapper = mapper);
        return this;
    }

    /// <summary>
    ///     Add MassTransit interoperability to this Rabbit MQ listening endpoint
    /// </summary>
    /// <param name="configure">Optionally configure the JSON serialization on this endpoint</param>
    /// <returns></returns>
    public RabbitMqListenerConfiguration UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        add(e =>
        {
            e.UseMassTransitInterop(configure);
        });
        return this;
    }

    /// <summary>
    ///     Add NServiceBus interoperability to this Rabbit MQ listening endpoint
    /// </summary>
    /// <returns></returns>
    public RabbitMqListenerConfiguration UseNServiceBusInterop()
    {
        add(e => e.UseNServiceBusInterop());
        return this;
    }

    /// <summary>
    ///     Create a "time to live" limit for messages in this queue. Sets the Rabbit MQ x-message-ttl argument on a queue
    /// </summary>
    public RabbitMqListenerConfiguration TimeToLive(TimeSpan time)
    {
        add(e => e.TimeToLive(time));
        return this;
    }

    /// <summary>
    ///     Make any customizations to the underlying Rabbit MQ Queue when Wolverine is building the
    ///     queues
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public RabbitMqListenerConfiguration ConfigureQueue(Action<IRabbitMqQueue> configure)
    {
        add(e => configure(e));
        
        return this;
    }

    /// <summary>
    /// Customize the dead letter queueing for this specific endpoint
    /// </summary>
    /// <param name="configure">Optional configuration</param>
    /// <returns></returns>
    public RabbitMqListenerConfiguration DeadLetterQueueing(DeadLetterQueue dlq)
    {
        add(e =>
        {
            e.DeadLetterQueue = dlq;
        });

        return this;
    }

    /// <summary>
    /// Remove all dead letter queueing declarations from this queue
    /// </summary>
    /// <returns></returns>
    public RabbitMqListenerConfiguration DisableDeadLetterQueueing()
    {
        add(e =>
        {
            e.DeadLetterQueue = null;
        });

        return this;
    }
}