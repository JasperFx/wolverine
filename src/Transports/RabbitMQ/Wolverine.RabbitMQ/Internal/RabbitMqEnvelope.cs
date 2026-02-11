namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqEnvelope : Envelope
{
    public RabbitMqEnvelope(RabbitMqListener listener, ulong deliveryTag)
    {
        RabbitMqListener = listener;
        DeliveryTag = deliveryTag;
    }

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

    public bool Acknowledged { get; private set; }

    internal async Task CompleteAsync()
    {
        await RabbitMqListener.CompleteAsync(DeliveryTag);
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
            await RabbitMqListener.CompleteAsync(DeliveryTag);
            Acknowledged = true;
        }

        await RabbitMqListener.RequeueAsync(this);
    }
}