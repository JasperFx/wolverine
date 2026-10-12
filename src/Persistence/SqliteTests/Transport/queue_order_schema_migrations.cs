using JasperFx;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;

namespace SqliteTests.Transport;

/// <summary>
/// GH-4862. On SQLite the opt-in ordering adds no column (the tie-breaker is the implicit rowid), only an
/// index on each queue table. Those indexes must be added to POPULATED legacy queue tables in place --
/// no table rebuild, which on SQLite would copy every row into a new table -- through both explicit queue
/// setup and the message store's normal resource migration path.
/// </summary>
public class queue_order_schema_migrations : SqliteContext
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task disabled_resource_migrations_leave_populated_legacy_queue_tables_unchanged()
    {
        using var database = Servers.CreateDatabase(nameof(queue_order_schema_migrations));
        var host = await startHostAsync(database, "legacy_without_opt_in", deterministicOrdering: false);

        try
        {
            var queue = queueFor(host, "legacy_without_opt_in");

            var readyRows = await populateAsync(database, queue.QueueTable, scheduled: false);
            var scheduledRows = await populateAsync(database, queue.ScheduledTable, scheduled: true);

            (await writeMigrationScriptAsync(database, queue.QueueTable)).ShouldBeEmpty();
            (await writeMigrationScriptAsync(database, queue.ScheduledTable)).ShouldBeEmpty();

            await host.GetRuntime().Storage.Admin.MigrateAsync();

            (await readRowsAsync(database, queue.QueueTable)).ShouldBe(readyRows);
            (await readRowsAsync(database, queue.ScheduledTable)).ShouldBe(scheduledRows);
            (await queue_ordering_is_opt_in.indexesOnAsync(database, queue.QueueTable)).ShouldBeEmpty();
            (await queue_ordering_is_opt_in.indexesOnAsync(database, queue.ScheduledTable)).ShouldBeEmpty();
            (await queue.CheckAsync()).ShouldBeTrue();

            await queue.SetupAsync(NullLogger.Instance);
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAsync(host);
        }
    }

    [Fact]
    public async Task ready_queue_upgrade_preserves_existing_messages()
    {
        using var database = Servers.CreateDatabase(nameof(queue_order_schema_migrations));
        var host = await startHostAsync(database, "ready_upgrade", deterministicOrdering: true);

        try
        {
            var queue = queueFor(host, "ready_upgrade");
            var table = queue.QueueTable;
            await makeLegacyAsync(database, table);
            var rows = await populateAsync(database, table, scheduled: false);

            assertOptInMigrationScript(await writeMigrationScriptAsync(database, table), table.Indexes.Single().Name);
            await queue.SetupAsync(NullLogger.Instance);

            // Same rows, same rowids: the index was created on the table, not by rebuilding it
            (await readRowsAsync(database, table)).ShouldBe(rows);
            await assertIndexAsync(database, table, "timestamp");
            (await queue.CheckAsync()).ShouldBeTrue();

            // Explicit setup is idempotent after the upgrade
            (await writeMigrationScriptAsync(database, table)).ShouldBeEmpty();
            await queue.SetupAsync(NullLogger.Instance);
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAsync(host);
        }
    }

    [Fact]
    public async Task scheduled_queue_upgrade_uses_message_store_resource_migration()
    {
        using var database = Servers.CreateDatabase(nameof(queue_order_schema_migrations));
        var host = await startHostAsync(database, "scheduled_upgrade", deterministicOrdering: true);

        try
        {
            var queue = queueFor(host, "scheduled_upgrade");
            var table = queue.ScheduledTable;
            await makeLegacyAsync(database, table);
            var rows = await populateAsync(database, table, scheduled: true);

            assertOptInMigrationScript(await writeMigrationScriptAsync(database, table), table.Indexes.Single().Name);

            // The queue tables are registered with the SQLite message store. Migrate through the admin entry
            // point to cover the ordinary startup/administrative resource path too.
            await host.GetRuntime().Storage.Admin.MigrateAsync();

            (await readRowsAsync(database, table)).ShouldBe(rows);
            await assertIndexAsync(database, table, "execution_time");
            (await queue.CheckAsync()).ShouldBeTrue();

            await host.GetRuntime().Storage.Admin.MigrateAsync();
            (await writeMigrationScriptAsync(database, table)).ShouldBeEmpty();
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAsync(host);
        }
    }

    private static async Task<IHost> startHostAsync(SqliteTestDatabase database, string queueName,
        bool deterministicOrdering)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
                var expression = opts.UseSqlitePersistenceAndTransport(database.ConnectionString);
                if (deterministicOrdering) expression.EnableDeterministicQueueOrdering();

                // Publish-only, so no listener touches the rows these tests plant
                opts.PublishAllMessages().ToSqliteQueue(queueName);
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();
            }).StartAsync(ct);
    }

    private static SqliteQueue queueFor(IHost host, string queueName)
    {
        return host.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>().Queues[queueName];
    }

    /// <summary>
    /// Back to the schema a queue table had before GH-4862: the same columns, and no index besides the
    /// primary key's own.
    /// </summary>
    private static async Task makeLegacyAsync(SqliteTestDatabase database, Table table)
    {
        await using var conn = await openAsync(database);
        foreach (var index in table.Indexes)
        {
            await using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP INDEX IF EXISTS {index.Name}";
            await drop.ExecuteNonQueryAsync(ct);
        }

        (await queue_ordering_is_opt_in.indexesOnAsync(database, table)).ShouldBeEmpty();
    }

    /// <summary>
    /// Three rows with the SAME timestamp (the ordinary case on SQLite), written in a known order
    /// </summary>
    private static async Task<List<(long RowId, string Id, byte Body)>> populateAsync(SqliteTestDatabase database,
        Table table, bool scheduled)
    {
        await using var conn = await openAsync(database);
        for (byte i = 1; i <= 3; i++)
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = scheduled
                ? $"INSERT INTO {table.Identifier} (id, body, message_type, execution_time, timestamp) VALUES (@id, @body, 'upgrade-test', '2100-01-01 00:00:00.0000000', '2026-01-01 00:00:00')"
                : $"INSERT INTO {table.Identifier} (id, body, message_type, timestamp) VALUES (@id, @body, 'upgrade-test', '2026-01-01 00:00:00')";
            insert.Parameters.AddWithValue("id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("body", new[] { i });
            await insert.ExecuteNonQueryAsync(ct);
        }

        var rows = await readRowsAsync(database, table);
        rows.Count.ShouldBe(3);
        rows.Select(x => x.RowId).Distinct().Count().ShouldBe(3);
        rows.Select(x => x.Body).ShouldBe(new byte[] { 1, 2, 3 });
        return rows;
    }

    private static async Task<List<(long RowId, string Id, byte Body)>> readRowsAsync(SqliteTestDatabase database,
        Table table)
    {
        await using var conn = await openAsync(database);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT rowid, id, body FROM {table.Identifier} ORDER BY rowid";

        var rows = new List<(long, string, byte)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), ((byte[])reader.GetValue(2)).Single()));
        }

        return rows;
    }

    private static async Task assertIndexAsync(SqliteTestDatabase database, Table table, string column)
    {
        var indexes = await queue_ordering_is_opt_in.indexesOnAsync(database, table);
        var index = table.Indexes.Single();
        indexes.Keys.ToArray().ShouldBe(new[] { index.Name });
        indexes[index.Name].ShouldContain($"({column})");
    }

    private static async Task<string> writeMigrationScriptAsync(SqliteTestDatabase database, Table table)
    {
        await using var conn = await openAsync(database);
        var migration = await SchemaMigration.DetermineAsync(conn, ct, table);
        await using var writer = new StringWriter();
        migration.WriteAllUpdates(writer, new SqliteMigrator(), AutoCreate.CreateOrUpdate);
        return writer.ToString().Trim();
    }

    private static void assertOptInMigrationScript(string script, string indexName)
    {
        var normalized = script.ToLowerInvariant();
        normalized.ShouldContain("create index");
        normalized.ShouldContain(indexName.ToLowerInvariant());
        normalized.ShouldNotContain("drop table");
        normalized.ShouldNotContain("drop index");
        normalized.ShouldNotContain("create table");
        normalized.ShouldNotContain("alter table");
        normalized.ShouldNotContain("insert into");
    }

    private static async Task<SqliteConnection> openAsync(SqliteTestDatabase database)
    {
        var conn = new SqliteConnection(database.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task stopAsync(IHost host)
    {
        await host.StopAsync(ct);
        host.Dispose();
    }
}
