using System.Threading.Tasks;
using TestingSupport.Compliance;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests
{

    public class RabbitMqTransportFixture : TransportComplianceFixture, IAsyncLifetime
    {
        public RabbitMqTransportFixture() : base($"rabbitmq://queue/{RabbitTesting.NextQueueName()}".ToUri())
        {

        }

        public async Task InitializeAsync()
        {
            var queueName = RabbitTesting.NextQueueName();
            OutboundAddress = $"rabbitmq://queue/{queueName}".ToUri();

            await SenderIs(opts =>
            {
                var listener = $"listener{RabbitTesting.Number}";

                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .DeclareQueue(queueName);

                opts.ListenToRabbitQueue(listener).UseForReplies();
            });

            await ReceiverIs(opts =>
            {
                opts.UseRabbitMq();
                opts.ListenToRabbitQueue(queueName);
            });
        }

        public Task DisposeAsync()
        {
            return Task.CompletedTask;
        }
    }

    [Collection("acceptance")]
    public class RabbitMqTransportComplianceTests : TransportCompliance<RabbitMqTransportFixture>
    {

    }
}
