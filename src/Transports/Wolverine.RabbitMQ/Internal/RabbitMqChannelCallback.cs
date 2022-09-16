using System.Threading.Tasks;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    public class RabbitMqChannelCallback : IChannelCallback
    {
        public static readonly RabbitMqChannelCallback Instance = new();

        private RabbitMqChannelCallback()
        {
        }

        public ValueTask CompleteAsync(Envelope envelope)
        {
            if (envelope is RabbitMqEnvelope e)
            {
                e.Complete();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DeferAsync(Envelope envelope)
        {
            if (envelope is RabbitMqEnvelope e)
            {
                return e.DeferAsync();
            }

            return ValueTask.CompletedTask;
        }
    }
}
