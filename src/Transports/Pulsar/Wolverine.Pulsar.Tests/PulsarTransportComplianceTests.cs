using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using TestingSupport.Compliance;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class PulsarTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    public PulsarTransportFixture() : base(null)
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
            opts.ListenToPulsarTopic(listener).UseForReplies();
        });

        await ReceiverIs(opts =>
        {
            opts.UsePulsar();
            opts.ListenToPulsarTopic(topicPath);
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
public class PulsarTransportComplianceTests : TransportCompliance<PulsarTransportFixture>;