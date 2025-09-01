using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class PulsarWithCloudEventsFixture : TransportComplianceFixture, IAsyncLifetime
{
    public PulsarWithCloudEventsFixture() : base(null)
    {
    }

    public async Task InitializeAsync()
    {
        var topic = Guid.NewGuid().ToString();
        var topicPath = $"persistent://public/default/compliance{topic}";
        OutboundAddress = PulsarEndpoint.UriFor(topicPath);

        await SenderIs(opts =>
        {
            var listener = $"persistent://public/default/replies{topic}";
            opts.UsePulsar(e => { });
            opts.ListenToPulsarTopic(listener).UseForReplies().InteropWithCloudEvents();
        });

        await ReceiverIs(opts =>
        {
            opts.UsePulsar();
            opts.ListenToPulsarTopic(topicPath).InteropWithCloudEvents();
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }

    public override void BeforeEach()
    {
        // A cooldown makes these tests far more reliable
        Thread.Sleep(3.Seconds());
    }
}

[Collection("acceptance")]
public class with_cloud_events : TransportCompliance<PulsarWithCloudEventsFixture>;