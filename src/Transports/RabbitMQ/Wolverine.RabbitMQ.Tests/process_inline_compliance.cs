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
    // GH-4520/GH-4559. The first of these was the literal "inline1" and the declaration below said
    // "quorum1" -- a copy-paste from QuorumQueueFixture, and the only name here that was not an "inline"
    // one. This fixture therefore declared quorum_queue_compliance's queue as CLASSIC, whichever class ran
    // second got a 406 "inequivalent arg 'x-queue-type'" that the old tolerate-and-continue path swallowed,
    // and that suite ran against a classic queue while its own assertion -- which read Wolverine's
    // configuration objects rather than the broker -- stayed green. A generated name cannot be typed wrong
    // and cannot collide with another fixture or an earlier run. Static so the whole class shares one pair:
    // xUnit builds a new fixture per test method.
    private static readonly string TheSendingQueue = RabbitTesting.NextQueueName();
    private static readonly string TheListeningQueue = RabbitTesting.NextQueueName();

    public ProcessInlineFixture() : base($"rabbitmq://queue/{TheSendingQueue}".ToUri())
    {
    }

    public async ValueTask InitializeAsync()
    {
        OutboundAddress = $"rabbitmq://queue/{TheSendingQueue}".ToUri();

        await SenderIs(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .DisableDeadLetterQueueing()
                .DeclareQueue(TheSendingQueue).ConfigureListeners(l => l.ProcessInline());

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "inline_sender");

            opts.ListenToRabbitQueue(TheListeningQueue).TelemetryEnabled(false);
        });

        await ReceiverIs(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .DisableDeadLetterQueueing()
                .ConfigureListeners(l => l.ProcessInline());

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "inline_receiver");

            opts.ListenToRabbitQueue(TheSendingQueue).TelemetryEnabled(false);
        });
    }

}

public class process_inline_compliance : TransportCompliance<ProcessInlineFixture>
{

}
