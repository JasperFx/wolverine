using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class BufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime {
	public BufferedComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://wolverine/buffered-receiver"), 120) { }

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
				.BufferedInMemory();
		});
	}

	public new async Task DisposeAsync() {
		await DisposeAsync();
	}
}

[Collection("acceptance")]
public class BufferedSendingAndReceivingCompliance : TransportCompliance<BufferedComplianceFixture> {
	[Fact]
	public virtual async Task dl_mechanics() {
		throwOnAttempt<DivideByZeroException>(1);
		throwOnAttempt<DivideByZeroException>(2);
		throwOnAttempt<DivideByZeroException>(3);

		await shouldMoveToErrorQueueOnAttempt(1);

		var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();

		var transport = runtime.Options.Transports.GetOrCreate<PubsubTransport>();
		var dlSubscription = transport.Topics[PubsubTransport.DeadLetterName].FindOrCreateSubscription();

		await dlSubscription.InitializeAsync(NullLogger.Instance);

		var pullResponse = await transport.SubscriberApiClient!.PullAsync(
			dlSubscription.Name,
			maxMessages: 5
		);

		pullResponse.ReceivedMessages.ShouldNotBeEmpty();
	}
}
