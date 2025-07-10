using IntegrationTests;
using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;


public class ProcessInlineFixture : TransportComplianceFixture, IAsyncLifetime
{
    public ProcessInlineFixture() : base($"rabbitmq://queue/inline1".ToUri())
    {
    }

    public async Task InitializeAsync()
    {
        OutboundAddress = $"rabbitmq://queue/inline1".ToUri();

        await SenderIs(opts =>
        {
            var listener = $"listener{RabbitTesting.Number}";

            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .DisableDeadLetterQueueing()
                .DeclareQueue("quorum1").ConfigureListeners(l => l.ProcessInline());

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "inline_sender");

            opts.ListenToRabbitQueue("inline2").TelemetryEnabled(false);
        });

        await ReceiverIs(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .DisableDeadLetterQueueing()
                .ConfigureListeners(l => l.ProcessInline());
            
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "inline_receiver");
            
            opts.ListenToRabbitQueue("inline1").TelemetryEnabled(false);
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

public class process_inline_compliance : TransportCompliance<ProcessInlineFixture>
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
