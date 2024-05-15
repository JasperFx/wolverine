using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime.Interop.MassTransit;

namespace Wolverine.RabbitMQ;

public class RabbitMqListenerConfiguration : ListenerConfiguration<RabbitMqListenerConfiguration, RabbitMqQueue>
{
    private readonly RabbitMqQueue _queue;

    public RabbitMqListenerConfiguration(RabbitMqQueue endpoint) : base(endpoint)
    {
        _queue = endpoint;
    }

    public string QueueName => _queue.QueueName;

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