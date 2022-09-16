using System.Threading.Tasks;
using TestingSupport.Compliance;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests
{
    public class InlineRabbitMqSendingFixture : SendingComplianceFixture, IAsyncLifetime
    {

        public InlineRabbitMqSendingFixture() : base($"rabbitmq://queue/{RabbitTesting.NextQueueName()}".ToUri())
        {

        }

        public async Task InitializeAsync()
        {
            var queueName = RabbitTesting.NextQueueName();
            OutboundAddress = $"rabbitmq://queue/{queueName}".ToUri();

            await SenderIs(opts =>
            {
                var listener = RabbitTesting.NextQueueName();

                opts
                    .ListenToRabbitQueue(listener)
                    .UseForReplies()
                    .ProcessInline();

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
            });

            await ReceiverIs(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.ListenToRabbitQueue(queueName).ProcessInline();
            });
        }

        public Task DisposeAsync()
        {
            return Task.CompletedTask;
        }

    }


    [Collection("acceptance")]
    public class InlineRabbitMqSendingComplianceTests : SendingCompliance<InlineRabbitMqSendingFixture>
    {

    }

}
