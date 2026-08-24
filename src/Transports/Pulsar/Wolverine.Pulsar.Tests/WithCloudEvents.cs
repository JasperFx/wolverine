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
    // GH-3797. The requeue, scheduled-retry and two dead-letter compliance tests used to be skipped here
    // as unimplemented Pulsar behaviour. They were not: UsePulsar()'s global failure rule swallowed the
    // fixture's own error policies and handed the failure to a continuation that did nothing on an
    // endpoint with no native resiliency configured. GH-4079/#4080 fixed that, and all four now run
    // unmodified from the base class. The full write-up is on PulsarTransportComplianceTests.
    //
    // will_move_to_dead_letter_queue_with_exception_match had also carried a CloudEvents-specific
    // serialization reason before that, which GH-3800 removed: ErrorCausingMessage records an exception
    // TYPE NAME rather than a live Exception, so it survives System.Text.Json and CloudEvents no longer
    // corrupts it. Neither reason applies now, so it too runs from the base class.
}