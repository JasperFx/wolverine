using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqEnvelope : Envelope
{
    public RabbitMqEnvelope(RabbitMqListener listener, ulong deliveryTag, IChannel deliveredOn)
    {
        RabbitMqListener = listener;
        DeliveryTag = deliveryTag;
        DeliveredOn = deliveredOn;
    }

    /// <summary>
    /// The channel this delivery actually arrived on. Delivery tags are scoped to a single
    /// channel and restart at 1 on each new one, so every settle path has to compare against
    /// this rather than just using whatever channel the listener currently holds. See
    /// <see cref="RabbitMqListener.CanSettle"/>.
    /// </summary>
    internal IChannel DeliveredOn { get; }

    /// <summary>
    /// This is only here for chaos testing. Don't use this for any
    /// other reason. You've been warned!
    /// </summary>
    /// <param name="tag"></param>
    internal void OverrideDeliveryTag(ulong tag)
    {
        DeliveryTag = tag;
    }

    internal RabbitMqListener RabbitMqListener { get;  }
    internal ulong DeliveryTag { get; private set;}

    public bool Acknowledged { get; internal set; }

    internal async Task CompleteAsync()
    {
        if (Acknowledged) return;

        await RabbitMqListener.CompleteAsync(this);
        Acknowledged = true;
    }

    internal async ValueTask DeferAsync()
    {
        // ACK the original delivery to release the prefetch slot,
        // then send a new copy to the queue for later processing.
        // Without ACKing first, the original message stays "in-flight" in RabbitMQ
        // and consumes a prefetch slot (blocking PreFetchCount(1) entirely).
        if (!Acknowledged)
        {
            await RabbitMqListener.CompleteAsync(this);
            Acknowledged = true;
        }

        await RabbitMqListener.RequeueAsync(this);
    }
}