using IntegrationTests;
using Marten;
using TestingSupport.Compliance;
using Weasel.Core;
using Wolverine.Marten;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class DurableComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public DurableComplianceFixture() : base(new Uri("asb://queue/durable-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureListeners(x => x.UseDurableInbox());

            opts.Services.AddMarten(store =>
            {
                store.Connection(Servers.PostgresConnectionString);
                store.DatabaseSchemaName = "sender";
                store.AutoCreateSchemaObjects = AutoCreate.All;
            }).IntegrateWithWolverine("sender").ApplyAllDatabaseChangesOnStartup();

            opts.ListenToAzureServiceBusQueue("durable-sender");
        });

        await ReceiverIs(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureListeners(x => x.UseDurableInbox());

            opts.Services.AddMarten(store =>
            {
                store.Connection(Servers.PostgresConnectionString);
                store.DatabaseSchemaName = "receiver";
                store.AutoCreateSchemaObjects = AutoCreate.All;
            }).IntegrateWithWolverine("receiver").ApplyAllDatabaseChangesOnStartup();

            opts.ListenToAzureServiceBusQueue("durable-receiver");
        });
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Collection("acceptance")]
    public class DurableSendingAndReceivingCompliance : TransportCompliance<DurableComplianceFixture>
    {
    }
}