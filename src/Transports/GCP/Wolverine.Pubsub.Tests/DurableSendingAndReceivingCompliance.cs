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
    public DurableComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://durable-receiver"),
        120)
    {
    }

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString();

        OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://durable-receiver.{id}");

        await SenderIs(opts =>
        {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
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
                .EnableSystemEndpoints()
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureSenders(x => x.UseDurableOutbox());

            opts.Services.AddMarten(store =>
            {
                store.Connection(Servers.PostgresConnectionString);
                store.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "receiver");

            opts.Services.AddResourceSetupOnStartup();

            opts.ListenToPubsubSubscription($"durable-receiver.{id}", $"durable-receiver.{id}");
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

}