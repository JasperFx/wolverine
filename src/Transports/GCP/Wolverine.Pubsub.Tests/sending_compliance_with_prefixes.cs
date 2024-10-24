using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PrefixedComplianceFixture : TransportComplianceFixture, IAsyncLifetime {
    public PrefixedComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://wolverine/foo.receiver"), 120) { }

    public async Task InitializeAsync() {
        var id = Guid.NewGuid().ToString();

        OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://wolverine/foo.receiver.{id}");

        await SenderIs(opts => {
            opts.UsePubsubTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .PrefixIdentifiers("foo")
                .EnableDeadLettering()
                .EnableSystemEndpoints();
        });

        await ReceiverIs(opts => {
            opts.UsePubsubTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .PrefixIdentifiers("foo")
                .EnableDeadLettering()
                .EnableSystemEndpoints();

            opts
                .ListenToPubsubTopic($"receiver.{id}")
                .Named("receiver");
        });
    }

    public new async Task DisposeAsync() {
        await DisposeAsync();
    }
}

[Collection("acceptance")]
public class PrefixedSendingAndReceivingCompliance : TransportCompliance<PrefixedComplianceFixture> {
    [Fact]
    public void prefix_was_applied_to_endpoint_for_the_receiver() {
        var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();
        var endpoint = runtime.Endpoints.EndpointByName("receiver");

        endpoint
            .ShouldBeOfType<PubsubEndpoint>()
            .Server.Topic.Name.TopicId.ShouldStartWith("foo.");

        endpoint
            .ShouldBeOfType<PubsubEndpoint>()
            .Server.Subscription.Name.SubscriptionId.ShouldStartWith("foo.");

        endpoint
            .ShouldBeOfType<PubsubEndpoint>()
            .Uri.Segments.Last().ShouldStartWith("foo.");
    }
}
