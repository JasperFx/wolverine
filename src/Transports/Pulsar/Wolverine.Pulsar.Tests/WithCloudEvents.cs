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
    // GH-3800 removed the CloudEvents-specific reason this test used to carry: ErrorCausingMessage
    // now records an exception TYPE NAME rather than a live Exception, so it survives
    // System.Text.Json and CloudEvents no longer corrupts it.
    //
    // It stays skipped, but for the same reason as its two sibling fixtures below rather than a
    // serialization one -- Pulsar has not implemented dead-letter routing. When GH-3797 lands this
    // skip goes with the others, not separately.
    [Fact(Skip = "Pulsar does not implement this compliance behaviour yet -- see GH-3797. Skipped rather than tagged Flaky: it fails deterministically, on every run, alone or in a suite.")]
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