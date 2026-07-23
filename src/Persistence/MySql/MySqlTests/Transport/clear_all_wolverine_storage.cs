using IntegrationTests;
using MySqlConnector;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.MySql.Transport;

namespace MySqlTests.Transport;

[Collection("mysql")]
public class clear_all_wolverine_storage : ClearAllWolverineStorageCompliance
{
    private const string SchemaName = "clear_all_storage";

    protected override async Task beforeHostAsync()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS {SchemaName}; CREATE DATABASE {SchemaName};";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, schema: SchemaName,
            transportSchema: SchemaName);

        // Subscriber only -- no listener, so nothing drains the queue out from under the
        // assertions before the reset runs.
        options.PublishAllMessages().ToMySqlQueue(QueueName);
    }

    protected override ValueTask sendToQueueAsync(Envelope envelope)
    {
        return ((MySqlQueue)theQueue).SendAsync(envelope);
    }
}
