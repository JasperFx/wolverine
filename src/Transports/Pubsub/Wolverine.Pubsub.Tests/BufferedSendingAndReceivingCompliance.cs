// using Microsoft.Extensions.DependencyInjection;
// using Microsoft.Extensions.Logging.Abstractions;
// using Shouldly;
// using Wolverine.ComplianceTests.Compliance;
// using Wolverine.Runtime;
// using Xunit;

// namespace Wolverine.Pubsub.Tests;

// public class BufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime {
// 	public BufferedComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://buffered-receiver"), 120) { }

// 	public async Task InitializeAsync() {
// 		DotNetEnv.Env.Load(".env.test");

// 		var topicName = Guid.NewGuid().ToString();

// 		OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://" + topicName);

// 		await SenderIs(opts => {
// 			opts
// 				.UsePubsubTesting()
// 				.AutoProvision()
// 				.SystemEndpointsAreEnabled(false);
// 			opts
// 				.PublishAllMessages()
// 				.ToPubsubTopic(topicName);
// 		});

// 		await ReceiverIs(opts => {
// 			opts
// 				.UsePubsubTesting()
// 				.AutoProvision()
// 				.SystemEndpointsAreEnabled(false);
// 			opts
// 				.ListenToPubsubTopic(topicName)
// 				.BufferedInMemory();
// 		});
// 	}

// 	public new async Task DisposeAsync() {
// 		await DisposeAsync();
// 	}
// }

// [Collection("acceptance")]
// public class BufferedSendingAndReceivingCompliance : TransportCompliance<BufferedComplianceFixture> {
// 	[Fact]
// 	public virtual async Task dl_mechanics() {
// 		throwOnAttempt<DivideByZeroException>(1);
// 		throwOnAttempt<DivideByZeroException>(2);
// 		throwOnAttempt<DivideByZeroException>(3);

// 		await shouldSucceedOnAttempt(3);

// 		var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();

// 		var transport = runtime.Options.Transports.GetOrCreate<PubsubTransport>();
// 		var dlSubscription = transport.Topics[PubsubTransport.DeadLetterEndpointName].FindOrCreateSubscription();

// 		await dlSubscription.InitializeAsync(NullLogger.Instance);

// 		var pullResponse = await transport.SubscriberApiClient!.PullAsync(
// 			dlSubscription.Name,
// 			maxMessages: 5
// 		);

// 		pullResponse.ReceivedMessages.ShouldNotBeEmpty();

// 	}
// }