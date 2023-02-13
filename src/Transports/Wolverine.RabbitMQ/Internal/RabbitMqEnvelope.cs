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
        // TEMP!!!!
        if (new Random().Next(0, 100) < 5)
        {
            RabbitMqListener.Complete(1);
        }
        RabbitMqListener.Complete(DeliveryTag);
        Acknowledged = true;
    }

    internal ValueTask DeferAsync()
    {
        Acknowledged = true;
        return RabbitMqListener.RequeueAsync(this);
    }
}