using IntegrationTests;
using JasperFx.Core;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;

namespace OracleTests.Transport;

[Collection("oracle")]
public class clear_all_wolverine_storage : ClearAllWolverineStorageCompliance
{
    private const string SchemaName = "WOLVERINE";

    protected override async Task beforeHostAsync()
    {
        // Oracle has no cheap "drop schema" — clear whatever a prior run left behind by name.
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        try
        {
            foreach (var table in new[]
                     {
                         $"WOLVERINE_QUEUE_{QueueName.ToUpperInvariant()}",
                         $"WOLVERINE_QUEUE_{QueueName.ToUpperInvariant()}_SCHEDULED"
                     })
            {
                try
                {
                    await using var cmd = conn.CreateCommand($"DROP TABLE {SchemaName}.{table} CASCADE CONSTRAINTS");
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (OracleException)
                {
                    // Table doesn't exist, nothing to do
                }
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.PersistMessagesWithOracle(Servers.OracleConnectionString, SchemaName)
            .EnableMessageTransport();

        // Registered through the listener rather than as a subscriber, unlike the other
        // providers: Oracle uppercases queue identifiers, but Uri.Host lowercases, so the
        // publishing.To(queue.Uri) inside ToOracleQueue() resolves a *second* endpoint over the
        // same physical tables. The polling interval keeps the listener from draining the queue
        // out from under the assertions.
        options.ListenToOracleQueue(QueueName).PollingInterval(1.Hours());
    }

    protected override ValueTask sendToQueueAsync(Envelope envelope)
    {
        return ((OracleQueue)theQueue).SendAsync(envelope);
    }
}
