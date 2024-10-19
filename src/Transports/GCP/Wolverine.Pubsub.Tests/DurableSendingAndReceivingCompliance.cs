using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Oakton.Resources;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Marten;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class DurableComplianceFixture : TransportComplianceFixture, IAsyncLifetime {
    public DurableComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://wolverine/receiver"), 120) { }

    public async Task InitializeAsync() {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        var id = Guid.NewGuid().ToString();

        OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://wolverine/receiver.{id}");

        await SenderIs(opts => {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true)
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureListeners(x => x.UseDurableInbox());

            opts.Services
                .AddMarten(store => {
                    store.Connection(Servers.PostgresConnectionString);
                    store.DatabaseSchemaName = "sender";
                })
                .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "sender");

            opts.Services.AddResourceSetupOnStartup();

            opts
                .ListenToPubsubTopic($"sender.{id}")
                .Named("sender");
        });

        await ReceiverIs(opts => {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true)
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureListeners(x => x.UseDurableInbox());

            opts.Services.AddMarten(store => {
                store.Connection(Servers.PostgresConnectionString);
                store.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "receiver");

            opts.Services.AddResourceSetupOnStartup();

            opts
                .ListenToPubsubTopic($"receiver.{id}")
                .Named("receiver");
        });
    }

    public new async Task DisposeAsync() {
        await DisposeAsync();
    }
}

public class DurableSendingAndReceivingCompliance : TransportCompliance<DurableComplianceFixture> {
    [Fact]
    public virtual async Task dl_mechanics() {
        throwOnAttempt<DivideByZeroException>(1);
        throwOnAttempt<DivideByZeroException>(2);
        throwOnAttempt<DivideByZeroException>(3);

        await shouldMoveToErrorQueueOnAttempt(1);

        var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();

        var transport = runtime.Options.Transports.GetOrCreate<PubsubTransport>();
        var topic = transport.Topics[PubsubTransport.DeadLetterName];

        await topic.InitializeAsync(NullLogger.Instance);

        var pullResponse = await transport.SubscriberApiClient!.PullAsync(
            topic.Server.Subscription.Name,
            maxMessages: 5
        );

        pullResponse.ReceivedMessages.ShouldNotBeEmpty();
    }
}
