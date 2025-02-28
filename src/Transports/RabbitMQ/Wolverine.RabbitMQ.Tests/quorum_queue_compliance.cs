using IntegrationTests;
using JasperFx.Core;
using Marten;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class QuorumQueueFixture : TransportComplianceFixture, IAsyncLifetime
{
    public QuorumQueueFixture() : base($"rabbitmq://queue/quorum1".ToUri())
    {
    }

    public async Task InitializeAsync()
    {
        OutboundAddress = $"rabbitmq://queue/quorum1".ToUri();

        await SenderIs(opts =>
        {
            var listener = $"listener{RabbitTesting.Number}";

            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .DisableDeadLetterQueueing()
                .DeclareQueue("quorum1")
                .UseQuorumQueues();

            opts.ListenToRabbitQueue("quorum2").TelemetryEnabled(false);
        });

        await ReceiverIs(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .DisableDeadLetterQueueing()
                .UseQuorumQueues();
            
            opts.ListenToRabbitQueue("quorum1").TelemetryEnabled(false);
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

public class quorum_queue_compliance : TransportCompliance<QuorumQueueFixture>
{
    [Fact]
    public void all_queues_are_declared_as_quorum()
    {
        var queues = theSender
            .GetRuntime()
            .Options
            .Transports
            .AllEndpoints()
            .OfType<RabbitMqQueue>()
            .Where(x => x.Role == EndpointRole.Application)
            .ToArray();
        
        queues.Any().ShouldBeTrue();
        foreach (var mqQueue in queues)
        {
            mqQueue.QueueType.ShouldBe(QueueType.quorum);
        }
        
    }
}

