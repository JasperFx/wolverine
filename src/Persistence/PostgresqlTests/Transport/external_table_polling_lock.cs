using IntegrationTests;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS.Transport;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

/// <summary>
///     GH-5676. A poll of an external message table takes pg_try_advisory_lock on its connection, which belongs to
///     the session, not the connection object. Closing the connection put the session back in the pool still holding
///     the lock -- Npgsql only resets a pooled session when it is next handed out -- so every other poller of an
///     external table on the database was shut out until then
/// </summary>
public class external_table_polling_lock
{
    [Fact]
    public async Task a_poll_leaves_the_lock_free_for_other_pollers()
    {
        var token = TestContext.Current.CancellationToken;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "polling_lock");
            }).StartAsync(token);

        var store = host.Services.GetRequiredService<IMessageStore>().As<IExternalDbTransportStore>();
        var table = new ExternalMessageTable(new DbObjectName("polling_lock_ext", "incoming"))
        {
            MessageType = typeof(string),

            // Not the default 12000, so another run against the same database cannot hold it
            AdvisoryLock = 56760
        };
        await store.MigrateExternalMessageTable(table);

        // Nothing to read, so the listener and receiver are never touched
        await store.PollForMessagesFromExternalTablesAsync(null!, host.GetRuntime(), table, null!, token);

        // A connection of its own, outside the store's pool, the way another node would ask
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Pooling = false };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@id)";
        cmd.Parameters.AddWithValue("id", table.AdvisoryLock);
        var attained = (bool)(await cmd.ExecuteScalarAsync(token))!;

        attained.ShouldBeTrue("The polling lock is still held by the poll's pooled session");

        await host.StopAsync(token);
    }
}
