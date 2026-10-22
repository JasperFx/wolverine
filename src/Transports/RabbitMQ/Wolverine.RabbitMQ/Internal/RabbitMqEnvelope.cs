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

    // Set once the copy is on the broker, so a retry of a deferral whose ack failed does not publish it again
    private bool _deferredCopyPublished;

    internal async ValueTask DeferAsync()
    {
        // Send a new copy to the queue for later processing, then ACK the original delivery to release its
        // prefetch slot. The ack still has to happen: without it the original stays "in-flight" in RabbitMQ and
        // consumes a prefetch slot (blocking PreFetchCount(1) entirely).
        //
        // GH-5123. The copy goes FIRST. This used to ack and then publish, so a copy that could not be published
        // -- a channel or connection that was not usable -- left the message nowhere at all. Publishing first
        // costs at worst a duplicate (the copy landed, the ack did not, and the broker redelivers the original),
        // which the durable inbox deduplicates. Publishing needs no prefetch slot, so this cannot block on one.
        if (!_deferredCopyPublished)
        {
            await RabbitMqListener.PublishDeferredCopyAsync(this);
            _deferredCopyPublished = true;
        }

        if (!Acknowledged)
        {
            await RabbitMqListener.CompleteAsync(this);
            Acknowledged = true;
        }
    }
}