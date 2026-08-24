using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class InlinePulsarTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    public InlinePulsarTransportFixture() : base(null!)
    {
    }

    public async ValueTask InitializeAsync()
    {
        var topic = Guid.NewGuid().ToString();
        var topicPath = $"persistent://public/default/{topic}";
        OutboundAddress = PulsarEndpointUri.Topic(topicPath);

        await ReceiverIs(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topicPath).ProcessInline();
        });

        await SenderIs(opts =>
        {
            var replyPath = $"persistent://public/default/replies-{topic}";
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(replyPath).UseForReplies().ProcessInline();
            opts.PublishAllMessages().ToPulsarTopic(topicPath).SendInline();
        });
    }


    public override void BeforeEach()
    {
        // These tests are *far* more reliable with a cooldown
        Thread.Sleep(3.Seconds());
    }
}

[Collection("acceptance")]
public class InlinePulsarTransportComplianceTests : TransportCompliance<InlinePulsarTransportFixture>
{
    // GH-3797. The requeue, scheduled-retry and two dead-letter compliance tests used to be skipped here
    // as unimplemented Pulsar behaviour. They were not: UsePulsar()'s global failure rule swallowed the
    // fixture's own error policies and handed the failure to a continuation that did nothing on an
    // endpoint with no native resiliency configured. GH-4079/#4080 fixed that, and all four now run
    // unmodified from the base class. The full write-up is on PulsarTransportComplianceTests.
}
