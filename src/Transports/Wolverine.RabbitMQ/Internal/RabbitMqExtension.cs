using Wolverine.Attributes;
using Wolverine.RabbitMQ.Internal;

[assembly: WolverineModule(typeof(RabbitMqExtension))]

namespace Wolverine.RabbitMQ.Internal
{
    public class RabbitMqExtension : IWolverineExtension
    {
        public void Configure(WolverineOptions options)
        {
            // this will force the transport collection
            // to add Rabbit MQ if it does not alreay
            // exist
            options.RabbitMqTransport();
        }
    }
}
