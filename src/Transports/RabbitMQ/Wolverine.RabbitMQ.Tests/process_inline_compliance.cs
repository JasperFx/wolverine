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

    public async ValueTask InitializeAsync()
    {
        OutboundAddress = $"rabbitmq://queue/inline1".ToUri();

        await SenderIs(opts =>
        {
            var listener = RabbitTesting.NextListenerName();

            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .DisableDeadLetterQueueing()
                // GH-4520: this said "quorum1" -- a copy-paste from QuorumQueueFixture, and the only name in
                // this fixture that is not an "inline" one. So this fixture declared quorum_queue_compliance's
                // queue as CLASSIC, and whichever class ran second got a 406 "inequivalent arg 'x-queue-type'"
                // that the old tolerate-and-continue path swallowed. quorum_queue_compliance then ran its whole
                // suite against a classic queue, and all_queues_are_declared_as_quorum still passed because it
                // asserts Wolverine's configuration objects rather than the broker's actual state.
                .DeclareQueue("inline1").ConfigureListeners(l => l.ProcessInline());

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

}

public class process_inline_compliance : TransportCompliance<ProcessInlineFixture>
{

}
