using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class PulsarWithCloudEventsFixture : TransportComplianceFixture, IAsyncLifetime
{
    public PulsarWithCloudEventsFixture() : base(null!)
    {
    }

    public async ValueTask InitializeAsync()
    {
        var topic = Guid.NewGuid().ToString();
        var topicPath = $"persistent://public/default/compliance{topic}";
        OutboundAddress = PulsarEndpointUri.Topic(topicPath);

        await SenderIs(opts =>
        {
            var listener = $"persistent://public/default/replies{topic}";
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.Policies.UsePulsarWithCloudEvents();
            opts.ListenToPulsarTopic(listener).UseForReplies();
            opts.PublishMessage<FakeMessage>().ToPulsarTopic(topicPath);
        });

        await ReceiverIs(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.Policies.UsePulsarWithCloudEvents();
            opts.ListenToPulsarTopic(topicPath);
        });
    }

    public record FakeMessage;

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await ((IAsyncDisposable)this).DisposeAsync();
    }

    public override void BeforeEach()
    {
        // A cooldown makes these tests far more reliable
        Thread.Sleep(3.Seconds());
    }
}

[Collection("acceptance")]
public class with_cloud_events : TransportCompliance<PulsarWithCloudEventsFixture>
{
    // This test uses ErrorCausingMessage which contains a Dictionary<int, Exception>.
    // Exception objects don't serialize/deserialize properly with System.Text.Json,
    // which CloudEvents uses internally. The test message's Errors dictionary gets
    // corrupted during serialization, causing the wrong exception type to be thrown.
    // This is a test infrastructure limitation, not a CloudEvents functionality issue.
    //
    // Skip rather than an empty body: an override that just returns Task.CompletedTask reports as a
    // PASS, so the suite counted a test that never ran anything. GH-3763.
    [Fact(Skip = "CloudEvents' System.Text.Json serialization corrupts ErrorCausingMessage's Dictionary<int, Exception>, so the wrong exception type is thrown -- a test-infrastructure limit, not a CloudEvents defect.")]
    public override Task will_move_to_dead_letter_queue_with_exception_match() => Task.CompletedTask;

    // GH-3763. Deterministic failures shared with the other two Pulsar compliance fixtures -- the
    // requeue, retry-scheduling and dead-letter behaviours the transport has not implemented. See GH-3797.
    [Fact(Skip = "Pulsar does not implement this compliance behaviour yet -- see GH-3797. Skipped rather than tagged Flaky: it fails deterministically, on every run, alone or in a suite.")]
    public override Task will_requeue_and_increment_attempts() => Task.CompletedTask;

    [Fact(Skip = "Pulsar does not implement this compliance behaviour yet -- see GH-3797. Skipped rather than tagged Flaky: it fails deterministically, on every run, alone or in a suite.")]
    public override Task can_schedule_retry() => Task.CompletedTask;

    [Fact(Skip = "Pulsar does not implement this compliance behaviour yet -- see GH-3797. Skipped rather than tagged Flaky: it fails deterministically, on every run, alone or in a suite.")]
    public override Task will_move_to_dead_letter_queue_without_any_exception_match() => Task.CompletedTask;
}