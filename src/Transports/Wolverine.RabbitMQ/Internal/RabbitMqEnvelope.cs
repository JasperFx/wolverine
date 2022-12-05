using System.Threading.Tasks;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqEnvelope : Envelope
{
    public RabbitMqEnvelope(RabbitMqListener listener, ulong deliveryTag)
    {
        RabbitMqListener = listener;
        DeliveryTag = deliveryTag;
    }

    internal RabbitMqListener RabbitMqListener { get; }
    internal ulong DeliveryTag { get; }

    public bool Acknowledged { get; private set; }

    internal void Complete()
    {
        RabbitMqListener.Complete(DeliveryTag);
        Acknowledged = true;
    }

    internal ValueTask DeferAsync()
    {
        Acknowledged = true;
        return RabbitMqListener.RequeueAsync(this);
    }
}