using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class TopicsComplianceFixture : TransportComplianceFixture, IAsyncLifetime {
    public TopicsComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://wolverine/topic1"), 120) { }

    public async Task InitializeAsync() {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        await SenderIs(opts => {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true);
            opts
                .PublishAllMessages()
                .ToPubsubTopic("topic1");
        });

        await ReceiverIs(opts => {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true);
            opts
                .ListenToPubsubTopic("topic1");
        });
    }

    public new async Task DisposeAsync() {
        await DisposeAsync();
    }
}

public class TopicAndSubscriptionSendingAndReceivingCompliance : TransportCompliance<TopicsComplianceFixture>;
