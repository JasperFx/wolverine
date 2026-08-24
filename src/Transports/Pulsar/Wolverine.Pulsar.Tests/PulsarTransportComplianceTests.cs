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
    // GH-3797. will_requeue_and_increment_attempts, can_schedule_retry and the two dead-letter tests
    // carried a skip here (and in the two sibling Pulsar fixtures) reading "Pulsar does not implement
    // this compliance behaviour yet". That diagnosis was wrong -- none of the three was a missing
    // transport feature.
    //
    // UsePulsar() registers PulsarNativeResiliencyPolicy's failure rule *globally*, with an
    // AlwaysMatches condition, so it sorted ahead of every opts.Policies.OnException<T>() rule the
    // compliance fixture configures. Its source claimed the failure for any PulsarListener whatsoever,
    // and PulsarNativeResiliencyContinuation then did nothing at all when the endpoint had no
    // retry-letter topic, no native dead-letter topic and no UseNativeRedelivery() -- which is exactly
    // this fixture. The message simply vanished, hence "No ending activity detected". The fixture's
    // Requeue() / ScheduleRetry() / MoveToErrorQueue() policies were never reached, so they were never
    // actually under test.
    //
    // GH-4079/#4080 made a continuation source able to decline: the Pulsar source now claims a failure
    // only when there is native resiliency to hand it to. With that in place all four behaviours work
    // over Pulsar and run unmodified from the base class, so there is nothing to override here.
    //
    // Red-baselined: restoring the pre-#4080 predicate (`envelope.Listener is PulsarListener`, without
    // the HasNativeResiliency check) fails all twelve -- these four across all three fixtures --
    // deterministically, and restoring it passes all twelve.
}
