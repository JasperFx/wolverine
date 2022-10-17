using IntegrationTests;
using Marten;
using TestingSupport.Compliance;
using Wolverine.Marten;

namespace Wolverine.AmazonSqs.Tests;

public class DurableSendingAndReceivingCompliance
{
    
}

public class DurableComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public DurableComplianceFixture() : base(new Uri("sqs://durable-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.Services.AddMarten(store =>
            {
                store.Connection(Servers.PostgresConnectionString);
                store.DatabaseSchemaName = "sender";
            }).IntegrateWithWolverine("sender").ApplyAllDatabaseChangesOnStartup();

            opts.ListenToSqsQueue("durable-sender")
                .UseDurableOutbox();
        });

        await ReceiverIs(opts =>
        {
            opts.UseAmazonSqsTransportLocally()
                .AutoProvision()
                .AutoPurgeOnStartup();
            
            opts.Services.AddMarten(store =>
            {
                store.Connection(Servers.PostgresConnectionString);
                store.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine("receiver").ApplyAllDatabaseChangesOnStartup();

            opts.ListenToSqsQueue("durable-receiver");
        });
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
    
    [Collection("acceptance")]
    public class DurableSendingAndReceivingCompliance: TransportCompliance<DurableComplianceFixture>
    {
    
    }
}