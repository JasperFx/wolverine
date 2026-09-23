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
    // GH-4559. These were the literals "quorum1" and "quorum2", and ProcessInlineFixture declared
    // "quorum1" too -- as a CLASSIC queue -- so whichever class ran second got a 406 and this suite spent
    // its life running against the wrong kind of queue. NextQueueName() is unique per process and per
    // call, so no other fixture and no earlier run can leave a queue of a different shape behind for this
    // one to inherit. Static so the whole class shares one pair: xUnit builds a new fixture per test
    // method, and per-instance names would leave 46 durable queues on the broker per run.
    private static readonly string TheSendingQueue = RabbitTesting.NextQueueName();
    private static readonly string TheListeningQueue = RabbitTesting.NextQueueName();

    public QuorumQueueFixture() : base($"rabbitmq://queue/{TheSendingQueue}".ToUri())
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
                .DeclareQueue(TheSendingQueue)
                .UseQuorumQueues();

            opts.ListenToRabbitQueue(TheListeningQueue).TelemetryEnabled(false);
        });

        await ReceiverIs(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .DisableDeadLetterQueueing()
                .UseQuorumQueues();

            opts.ListenToRabbitQueue(TheSendingQueue).TelemetryEnabled(false);
        });
    }

}

public class quorum_queue_compliance : TransportCompliance<QuorumQueueFixture>
{
    /// <summary>
    /// GH-4559. Asks the BROKER what the queues are, because the obvious version of this test cannot fail.
    /// It used to read <c>RabbitMqQueue.QueueType</c> -- a property Wolverine set on its own endpoint object
    /// during configuration -- so it asserted what Wolverine intended to declare and would have passed
    /// against a broker that was switched off. It did pass, for as long as ProcessInlineFixture was
    /// declaring this suite's queue as classic and the broker's 406 was being swallowed.
    /// </summary>
    [Fact]
    public async Task all_queues_are_declared_as_quorum()
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

        using var probe = await RabbitManagementProbe.RequireAsync(TestContext.Current.CancellationToken);

        // Collected rather than asserted one at a time so the failure names every offending queue and what
        // the broker says it actually is
        var wrong = new List<string>();
        foreach (var queue in queues)
        {
            var actual = await probe.GetQueueTypeAsync(queue.QueueName,
                token: TestContext.Current.CancellationToken);

            if (actual != "quorum")
            {
                wrong.Add($"{queue.QueueName} is '{actual ?? "missing from the broker"}'");
            }
        }

        wrong.ShouldBeEmpty();
    }
}

