using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class InlineRabbitMqTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    public InlineRabbitMqTransportFixture() : base($"rabbitmq://queue/{RabbitTesting.NextQueueName()}".ToUri())
    {
    }

    public async Task InitializeAsync()
    {
        var queueName = RabbitTesting.NextQueueName() + "_inline";
        OutboundAddress = $"rabbitmq://queue/{queueName}".ToUri();

        await SenderIs(opts =>
        {
            var listener = RabbitTesting.NextQueueName() + "_inline";

            opts
                .ListenToRabbitQueue(listener)
                .ProcessInline().TelemetryEnabled(false);

            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
        });

        await ReceiverIs(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

            opts.ListenToRabbitQueue(queueName).ProcessInline().TelemetryEnabled(false);
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

public class InlineRabbitMqTransportComplianceTests : TransportCompliance<InlineRabbitMqTransportFixture>;