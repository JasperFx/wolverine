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

		var id = Guid.NewGuid().ToString();

		OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://wolverine/buffered-receiver.{id}");

		await SenderIs(opts => {
			opts
				.UsePubsubTesting()
				.AutoProvision()
				.AutoPurgeOnStartup();
		});

		await ReceiverIs(opts => {
			opts
				.UsePubsubTesting()
				.AutoProvision()
				.AutoPurgeOnStartup()
				.EnableAllNativeDeadLettering()
				.SystemEndpointsAreEnabled(true);

			opts
				.ListenToPubsubTopic($"buffered-receiver.{id}")
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
		var dl = transport.Topics[PubsubTransport.DeadLetterName];

		await dl.InitializeAsync(NullLogger.Instance);

		var pullResponse = await transport.SubscriberApiClient!.PullAsync(
			dl.Server.Subscription.Name,
			maxMessages: 1
		);

		pullResponse.ReceivedMessages.ShouldNotBeEmpty();
	}
}
