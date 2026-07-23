using IntegrationTests;
using JasperFx.Core;
using Npgsql;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

// PostgresqlContext is the usual base for this suite, but the compliance harness already
// occupies the base class, so the collection marker is applied directly.
[Collection("marten")]
public class clear_all_wolverine_storage : ClearAllWolverineStorageCompliance
{
    protected override async Task beforeHostAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("clear_all_storage");
        await conn.CloseAsync();
    }

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString,
            schema: "clear_all_storage", transportSchema: "clear_all_storage");

        // Subscriber only -- no listener, so nothing drains the queue out from under the
        // assertions before the reset runs.
        options.PublishAllMessages().ToPostgresqlQueue(QueueName);
    }

    protected override ValueTask sendToQueueAsync(Envelope envelope)
    {
        return ((PostgresqlQueue)theQueue).SendAsync(envelope);
    }
}
