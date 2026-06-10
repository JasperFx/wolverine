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

    /// <summary>
    /// Mark the target queue or exchange as owned by an external system. Wolverine will not declare
    /// (create) it at startup or delete it during <c>resources teardown</c>, even when
    /// <c>AutoProvision()</c> is enabled, and will not set up or tear down its bindings. Use this when
    /// the calling identity lacks the <c>configure</c>/<c>delete</c> permissions for the resource.
    /// See https://github.com/JasperFx/wolverine/issues/3064.
    /// </summary>
    public RabbitMqSubscriberConfiguration ExternallyOwned()
    {
        add(e => e.IsExternallyOwned = true);
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
    /// <summary>
    /// Mark this exchange as owned by an external system. Wolverine will not declare (create) it at
    /// startup or delete it during <c>resources teardown</c>, even when <c>AutoProvision()</c> is
    /// enabled, and will not set up or tear down its bindings. Use this when the calling identity lacks
    /// the <c>configure</c>/<c>delete</c> permissions for the exchange. See https://github.com/JasperFx/wolverine/issues/3064.
    /// </summary>
    public RabbitMqExchangeConfiguration ExternallyOwned()
    {
        add(e => e.IsExternallyOwned = true);
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
