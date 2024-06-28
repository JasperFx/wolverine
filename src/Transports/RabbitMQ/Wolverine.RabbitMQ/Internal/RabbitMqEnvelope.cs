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

    internal ValueTask DeferAsync()
    {
        Acknowledged = true;
        return RabbitMqListener.RequeueAsync(this);
    }
}