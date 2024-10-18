using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PrefixedComplianceFixture : TransportComplianceFixture, IAsyncLifetime {
    public PrefixedComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://wolverine/foo.buffered-receiver"), 120) { }

    public async Task InitializeAsync() {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        var topicName = $"test.{Guid.NewGuid()}";

        OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://wolverine/foo.{topicName}");

        await SenderIs(opts => {
            opts.UsePubsubTesting()
                .PrefixIdentifiers("foo")
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true)
                .AutoProvision();

        });

        await ReceiverIs(opts => {
            opts.UsePubsubTesting()
                .PrefixIdentifiers("foo")
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true)
                .AutoProvision();

            opts.ListenToPubsubTopic(topicName).Named("receiver");
        });
    }

    public new Task DisposeAsync() {
        return Task.CompletedTask;
    }
}

public class PrefixedSendingAndReceivingCompliance : TransportCompliance<PrefixedComplianceFixture> {
    [Fact]
    public void prefix_was_applied_to_queues_for_the_receiver() {
        var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();

        runtime.Endpoints.EndpointByName("receiver")
            .ShouldBeOfType<PubsubSubscription>()
            .Name.SubscriptionId.ShouldStartWith("foo.");
    }
}
