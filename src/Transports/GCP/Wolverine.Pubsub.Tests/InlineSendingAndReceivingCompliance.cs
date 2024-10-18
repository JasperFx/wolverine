using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class InlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime {
    public InlineComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://wolverine/inline-receiver"), 120) { }

    public async Task InitializeAsync() {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        var topicName = $"test.{Guid.NewGuid()}";

        OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://wolverine/{topicName}");

        await SenderIs(opts => {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true);
            opts
                .PublishAllMessages()
                .ToPubsubTopic(topicName);
        });

        await ReceiverIs(opts => {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true);
            opts
                .ListenToPubsubTopic(topicName)
                .ProcessInline();
        });
    }

    public new async Task DisposeAsync() {
        await DisposeAsync();
    }
}

public class InlineSendingAndReceivingCompliance : TransportCompliance<InlineComplianceFixture>;
