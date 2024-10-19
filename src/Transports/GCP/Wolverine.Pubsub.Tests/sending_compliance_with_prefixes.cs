using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PrefixedComplianceFixture : TransportComplianceFixture, IAsyncLifetime {
    public PrefixedComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://wolverine/foo.receiver"), 120) { }

    public async Task InitializeAsync() {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        var id = Guid.NewGuid().ToString();

        OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://wolverine/foo.receiver.{id}");

        await SenderIs(opts => {
            opts.UsePubsubTesting()
                .PrefixIdentifiers("foo")
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true)
                .AutoProvision();

            opts
                .ListenToPubsubTopic($"foo.sender.{id}")
                .Named("sender");
        });

        await ReceiverIs(opts => {
            opts.UsePubsubTesting()
                .PrefixIdentifiers("foo")
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true)
                .AutoProvision();

            opts
                .ListenToPubsubTopic($"foo.receiver.{id}")
                .Named("receiver");
        });
    }

    public new Task DisposeAsync() {
        return Task.CompletedTask;
    }
}

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
