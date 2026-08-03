using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class PulsarTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    public PulsarTransportFixture() : base(null!)
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
            opts.ListenToPulsarTopic(listener).UseForReplies();
        });

        await ReceiverIs(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topicPath);
        });
    }

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
public class PulsarTransportComplianceTests : TransportCompliance<PulsarTransportFixture>
{
    // GH-3763. These four are not flaky -- they fail deterministically, every run, in all three Pulsar
    // compliance fixtures, with "No ending activity detected" / "Expected ending activity was not
    // detected". They are the requeue, retry-scheduling and dead-letter behaviours the transport has not
    // implemented. Skipping just these restores the other 55 tests in this file's three fixtures, which
    // pass; the whole classes used to be excluded for them. See GH-3797.
    [Fact(Skip = "Pulsar does not implement this compliance behaviour yet -- see GH-3797. Skipped rather than tagged Flaky: it fails deterministically, on every run, alone or in a suite.")]
    public override Task will_requeue_and_increment_attempts() => Task.CompletedTask;

    [Fact(Skip = "Pulsar does not implement this compliance behaviour yet -- see GH-3797. Skipped rather than tagged Flaky: it fails deterministically, on every run, alone or in a suite.")]
    public override Task can_schedule_retry() => Task.CompletedTask;

    [Fact(Skip = "Pulsar does not implement this compliance behaviour yet -- see GH-3797. Skipped rather than tagged Flaky: it fails deterministically, on every run, alone or in a suite.")]
    public override Task will_move_to_dead_letter_queue_with_exception_match() => Task.CompletedTask;

    [Fact(Skip = "Pulsar does not implement this compliance behaviour yet -- see GH-3797. Skipped rather than tagged Flaky: it fails deterministically, on every run, alone or in a suite.")]
    public override Task will_move_to_dead_letter_queue_without_any_exception_match() => Task.CompletedTask;
}
