using System.Threading.Tasks;

namespace Wolverine.RabbitMQ.Internal
{
    public class RabbitMqEnvelope : Envelope
    {
        public RabbitMqEnvelope(RabbitMqListener listener, ulong deliveryTag)
        {
            Listener = listener;
            DeliveryTag = deliveryTag;
        }

        internal RabbitMqListener Listener { get; }
        internal ulong DeliveryTag { get; }

        public bool Acknowledged { get; private set; }

        internal void Complete()
        {
            Listener.Complete(DeliveryTag);
            Acknowledged = true;
        }

        internal ValueTask DeferAsync()
        {
            Acknowledged = true;
            return Listener.RequeueAsync(this);
        }
    }
}
