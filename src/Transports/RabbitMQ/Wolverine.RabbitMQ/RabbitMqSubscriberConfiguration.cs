using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime.Interop.MassTransit;

namespace Wolverine.RabbitMQ;

public class
    RabbitMqSubscriberConfiguration : InteroperableSubscriberConfiguration<RabbitMqSubscriberConfiguration, RabbitMqEndpoint, IRabbitMqEnvelopeMapper, RabbitMqEnvelopeMapper>
{
    internal RabbitMqSubscriberConfiguration(RabbitMqEndpoint endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure this Rabbit MQ endpoint for interoperability with MassTransit
    /// </summary>
    /// <param name="configure">Optionally configure the JSON serialization for MassTransit</param>
    /// <returns></returns>
    public RabbitMqSubscriberConfiguration UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        add(e => e.UseMassTransitInterop(configure));
        return this;
    }

    /// <summary>
    ///     Configure this Rabbit MQ endpoint for interoperability with NServiceBus
    /// </summary>
    /// <returns></returns>
    public RabbitMqSubscriberConfiguration UseNServiceBusInterop()
    {
        add(e => e.UseNServiceBusInterop());
        return this;
    }

    /// <summary>
    /// Customize the dead letter queueing for this specific publish queue.
    /// </summary>
    /// <param name="dlq">The dead letter queue configuration to apply.</param>
    /// <returns></returns>
    public RabbitMqSubscriberConfiguration DeadLetterQueueing(DeadLetterQueue dlq)
    {
        add(e =>
        {
            if (e is not RabbitMqQueue queue)
            {
                throw new InvalidOperationException(
                    "DeadLetterQueueing() is only supported for publish-to-queue endpoints.");
            }

            queue.DeadLetterQueue = dlq.Clone();
        });

        return this;
    }

    /// <summary>
    /// Remove dead letter queueing from this specific publish queue.
    /// </summary>
    /// <returns></returns>
    public RabbitMqSubscriberConfiguration DisableDeadLetterQueueing()
    {
        add(e =>
        {
            if (e is not RabbitMqQueue queue)
            {
                throw new InvalidOperationException(
                    "DisableDeadLetterQueueing() is only supported for publish-to-queue endpoints.");
            }

            queue.DeadLetterQueue = null;
        });

        return this;
    }
}

public class RabbitMqExchangeConfiguration : InteroperableSubscriberConfiguration<RabbitMqExchangeConfiguration, RabbitMqExchange, IRabbitMqEnvelopeMapper, RabbitMqEnvelopeMapper>
{
    internal RabbitMqExchangeConfiguration(RabbitMqExchange endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure this Rabbit MQ endpoint for interoperability with MassTransit
    /// </summary>
    /// <param name="configure">Optionally configure the JSON serialization for MassTransit</param>
    /// <returns></returns>
    public RabbitMqExchangeConfiguration UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        add(e => e.UseMassTransitInterop(configure));
        return this;
    }

    /// <summary>
    ///     Configure this Rabbit MQ endpoint for interoperability with NServiceBus
    /// </summary>
    /// <returns></returns>
    public RabbitMqExchangeConfiguration UseNServiceBusInterop()
    {
        add(e => e.UseNServiceBusInterop());
        return this;
    }

    /// <summary>
    ///     Modify the exchange type, the default is fan out
    /// </summary>
    /// <param name="exchangeType"></param>
    /// <returns></returns>
    public RabbitMqExchangeConfiguration ExchangeType(ExchangeType exchangeType)
    {
        add(e => e.ExchangeType = exchangeType);
        return this;
    }
}
