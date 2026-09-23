using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;


public class StreamQueueFixture : TransportComplianceFixture, IAsyncLifetime
{
    // GH-4559. Were the literals "stream1" and "stream2", which any other fixture could declare with a
    // different shape -- and one did, to QuorumQueueFixture. A generated name is unique per process and per
    // call, so nothing else and no earlier run can leave a queue of the wrong kind behind for this fixture
    // to inherit. Static so the whole class shares one pair: xUnit builds a new fixture per test method.
    private static readonly string TheSendingQueue = RabbitTesting.NextQueueName();
    private static readonly string TheListeningQueue = RabbitTesting.NextQueueName();

    public StreamQueueFixture() : base($"rabbitmq://queue/{TheSendingQueue}".ToUri())
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
                .DisableDeadLetterQueueing()
                .DeclareQueue(TheSendingQueue)
                .UseStreamsAsQueues();

            opts.ListenToRabbitQueue(TheListeningQueue).TelemetryEnabled(false);
        });

        await ReceiverIs(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.UseRabbitMq()
                .DisableDeadLetterQueueing()
                .UseStreamsAsQueues();

            opts.ListenToRabbitQueue(TheSendingQueue).TelemetryEnabled(false);
        });
    }

}

public class stream_queue_compliance : TransportCompliance<StreamQueueFixture>
{
    /// <summary>
    /// GH-4559. Asks the BROKER what the queues are. This used to read <c>RabbitMqQueue.QueueType</c> -- a
    /// property Wolverine set on its own endpoint object during configuration -- so it asserted what
    /// Wolverine intended to declare and would have passed against a broker that was switched off. Its twin
    /// in <see cref="quorum_queue_compliance"/> did exactly that for as long as another fixture was
    /// declaring the same queue as classic.
    /// </summary>
    [Fact]
    public async Task all_queues_are_declared_as_stream()
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

            if (actual != "stream")
            {
                wrong.Add($"{queue.QueueName} is '{actual ?? "missing from the broker"}'");
            }
        }

        wrong.ShouldBeEmpty();
    }
}
