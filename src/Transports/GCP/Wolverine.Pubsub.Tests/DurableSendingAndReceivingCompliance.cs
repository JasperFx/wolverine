using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Marten;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class DurableComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public DurableComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://wolverine/durable-receiver"),
        120)
    {
    }

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString();

        OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://wolverine/durable-receiver.{id}");

        await SenderIs(opts =>
        {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .EnableDeadLettering()
                .EnableSystemEndpoints()
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureSenders(x => x.UseDurableOutbox());

            opts.Services
                .AddMarten(store =>
                {
                    store.Connection(Servers.PostgresConnectionString);
                    store.DatabaseSchemaName = "sender";
                })
                .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "sender");

            opts.Services.AddResourceSetupOnStartup();
        });

        await ReceiverIs(opts =>
        {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .EnableDeadLettering()
                .EnableSystemEndpoints()
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureSenders(x => x.UseDurableOutbox());

            opts.Services.AddMarten(store =>
            {
                store.Connection(Servers.PostgresConnectionString);
                store.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "receiver");

            opts.Services.AddResourceSetupOnStartup();

            opts.ListenToPubsubTopic($"durable-receiver.{id}");
        });
    }

    public new async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("acceptance")]
public class DurableSendingAndReceivingCompliance : TransportCompliance<DurableComplianceFixture>
{
    [Fact]
    public virtual async Task dl_mechanics()
    {
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
            1
        );

        pullResponse.ReceivedMessages.ShouldNotBeEmpty();
    }
}