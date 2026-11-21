using IntegrationTests;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Transport;
using Wolverine.Tracking;

namespace MySqlTests.Transport;

/// <summary>
///     GH-5676. A poll of an external message table takes GET_LOCK on its connection, and GET_LOCK belongs to the
///     session, not the connection object. Closing the connection put the session back in the pool still holding the
///     lock, so every other poller of an external table on the server -- another node, or the next host in the same
///     test run -- was shut out until the pool got round to resetting it. That is the external_message_tables
///     end to end test that waited a whole minute for a message nobody polled
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
                opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, "polling_lock");
            }).StartAsync(token);

        var store = host.Services.GetRequiredService<IMessageStore>().As<IExternalDbTransportStore>();
        var table = new ExternalMessageTable(new DbObjectName("polling_lock_ext", "incoming"))
        {
            MessageType = typeof(string),

            // Not the default 12000, so another run against the same server cannot hold it
            AdvisoryLock = 56760
        };
        await store.MigrateExternalMessageTable(table);

        // Nothing to read, so the listener and receiver are never touched
        await store.PollForMessagesFromExternalTablesAsync(null!, host.GetRuntime(), table, null!, token);

        // A connection of its own, outside the store's pool, the way another node would ask
        var builder = new MySqlConnectionStringBuilder(Servers.MySqlConnectionString) { Pooling = false };
        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync(token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT IS_USED_LOCK(@name)";
        cmd.Parameters.AddWithValue("@name", $"wolverine_{table.AdvisoryLock}");
        var holder = await cmd.ExecuteScalarAsync(token);

        (holder is null or DBNull).ShouldBeTrue($"The polling lock is still held by connection {holder} after the poll");

        await host.StopAsync(token);
    }
}
