using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Weasel.Core;
using Weasel.MySql;
using Weasel.MySql.Tables;
using Wolverine;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Runtime;

namespace MySqlTests.Transport;

/// <summary>
/// GH-4862. MySQL refuses to add an AUTO_INCREMENT column unless the same statement makes it a key, so the
/// opt-in ordering column has to be added to populated legacy queue tables in one ALTER TABLE, through both
/// explicit queue setup and the message store's normal resource migration path.
/// </summary>
[Collection("mysql")]
public class queue_order_schema_migrations
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task disabled_resource_migrations_leave_populated_legacy_queue_tables_unchanged()
    {
        var schema = await newSchemaAsync();
        // Short: the legacy idx_{table}_execution_time name has no length guard and MySQL caps identifiers at 64
        const string queueName = "legacy";
        var readyIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var scheduledIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        IHost? host = null;

        try
        {
            host = await startHostAsync(schema, queueName, deterministicOrdering: false);
            var (runtime, queue) = await stopListenerAsync(host, queueName);

            var readyTable = queue.QueueTableFor(queue.DataSource);
            var scheduledTable = queue.ScheduledTableFor(queue.DataSource);
            await createLegacyReadyQueueAsync(schema, readyTable.Identifier.Name, readyIds);
            await createLegacyScheduledQueueAsync(schema, scheduledTable.Identifier.Name, scheduledIds);

            assertLegacyMigrationScript(await writeMigrationScriptAsync(readyTable));
            assertLegacyMigrationScript(await writeMigrationScriptAsync(scheduledTable));

            await runtime.Storage.Admin.MigrateAsync();

            await assertLegacyMessagesAndIndexesAsync(readyTable, readyIds);
            await assertLegacyMessagesAndIndexesAsync(scheduledTable, scheduledIds);
            (await queue.CheckAsync()).ShouldBeTrue();

            await queue.SetupAsync(NullLogger.Instance);
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Fact]
    public async Task ready_queue_upgrade_preserves_existing_messages()
    {
        var schema = await newSchemaAsync();
        const string queueName = "ready_upgrade";
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        IHost? host = null;

        try
        {
            host = await startHostAsync(schema, queueName);
            var (_, queue) = await stopListenerAsync(host, queueName);

            var table = queue.QueueTableFor(queue.DataSource);
            await createLegacyReadyQueueAsync(schema, table.Identifier.Name, ids);

            assertOptInMigrationScript(await writeMigrationScriptAsync(table), table);
            await queue.SetupAsync(NullLogger.Instance);

            await assertMessagesAndIndexesAsync(table, ids);
            (await queue.CheckAsync()).ShouldBeTrue();

            // Explicit setup is idempotent after the upgrade.
            (await writeMigrationScriptAsync(table)).Trim().ShouldBeEmpty();
            await queue.SetupAsync(NullLogger.Instance);
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Fact]
    public async Task scheduled_queue_upgrade_uses_message_store_resource_migration()
    {
        var schema = await newSchemaAsync();
        const string queueName = "scheduled_upgrade";
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        IHost? host = null;

        try
        {
            host = await startHostAsync(schema, queueName);
            var (runtime, queue) = await stopListenerAsync(host, queueName);

            var table = queue.ScheduledTableFor(queue.DataSource);
            await createLegacyScheduledQueueAsync(schema, table.Identifier.Name, ids);

            assertOptInMigrationScript(await writeMigrationScriptAsync(table), table);

            // The queue tables are registered with the MySQL message store in MySqlTransport.ConnectAsync().
            // Migrate through the admin entry point to cover the ordinary startup/administrative path too.
            await runtime.Storage.Admin.MigrateAsync();

            await assertMessagesAndIndexesAsync(table, ids);
            (await queue.CheckAsync()).ShouldBeTrue();

            (await writeMigrationScriptAsync(table)).Trim().ShouldBeEmpty();
            await runtime.Storage.Admin.MigrateAsync();
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    private static async Task<string> newSchemaAsync()
    {
        var schema = $"queue_order_{Guid.NewGuid():N}"[..40];
        await executeAsync($"CREATE DATABASE IF NOT EXISTS {schema}");
        return schema;
    }

    private static async Task<IHost> startHostAsync(string schema, string queueName, bool deterministicOrdering = true)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var expression = opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, schema,
                    transportSchema: schema);
                if (deterministicOrdering) expression.EnableDeterministicQueueOrdering();

                opts.ListenToMySqlQueue(queueName).PollingInterval(1.Hours());
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();
            }).StartAsync(ct);
    }

    private static async Task<(IWolverineRuntime Runtime, MySqlQueue Queue)> stopListenerAsync(IHost host,
        string queueName)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var queue = runtime.Options.Transports.GetOrCreate<MySqlTransport>().Queues[queueName];
        await runtime.Endpoints.StopListenerAsync(queue, ct);
        return (runtime, queue);
    }

    /// <summary>
    /// Recreate the table exactly as a release without the option builds it, populated with two rows that
    /// share one timestamp.
    /// </summary>
    private static async Task createLegacyReadyQueueAsync(string schema, string table, IReadOnlyList<Guid> ids)
    {
        await recreateAsync(new QueueTable(schema, table, deterministicQueueOrdering: false));
        await executeAsync($"""
                            INSERT INTO {schema}.{table} (id, body, message_type, timestamp)
                            VALUES (@id1, @body1, 'upgrade-test', '2026-01-01 00:00:00'),
                                   (@id2, @body2, 'upgrade-test', '2026-01-01 00:00:00');
                            """, ids);
    }

    private static async Task createLegacyScheduledQueueAsync(string schema, string table, IReadOnlyList<Guid> ids)
    {
        await recreateAsync(new ScheduledMessageTable(schema, table, deterministicQueueOrdering: false));
        await executeAsync($"""
                            INSERT INTO {schema}.{table} (id, body, message_type, execution_time, timestamp)
                            VALUES (@id1, @body1, 'upgrade-test', '2100-01-01 00:00:00', '2026-01-01 00:00:00'),
                                   (@id2, @body2, 'upgrade-test', '2100-01-01 00:00:00', '2026-01-01 00:00:00');
                            """, ids);
    }

    private static async Task recreateAsync(Table legacy)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);
        await legacy.DropAsync(conn, ct);
        await legacy.CreateAsync(conn, ct);
    }

    private static async Task assertMessagesAndIndexesAsync(Table table, IReadOnlyList<Guid> ids)
    {
        var schema = table.Identifier.Schema!;
        var tableName = table.Identifier.Name;
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);

        await using (var rows = conn.CreateCommand())
        {
            rows.CommandText = $"SELECT id, body, queue_order FROM {schema}.{tableName}";
            await using var reader = await rows.ExecuteReaderAsync(ct);
            var actual = new Dictionary<Guid, (byte[] Body, long QueueOrder)>();
            while (await reader.ReadAsync(ct))
            {
                actual.Add(reader.GetGuid(0), ((byte[])reader.GetValue(1), reader.GetInt64(2)));
            }

            actual.Count.ShouldBe(2);
            actual.Keys.ShouldBe(ids, ignoreOrder: true);
            actual[ids[0]].Body.ShouldBe(new byte[] { 1 });
            actual[ids[1]].Body.ShouldBe(new byte[] { 2 });
            actual.Values.Select(x => x.QueueOrder).Distinct().Count().ShouldBe(2);
        }

        var indexes = await queue_ordering_is_opt_in.readIndexesAsync(conn, schema, tableName);
        foreach (var index in table.Indexes)
        {
            indexes.TryGetValue(index.Name, out var columns).ShouldBeTrue($"Missing index {index.Name}");
            columns!.ShouldBe(index.Columns);
        }

        indexes.Count.ShouldBe(table.Indexes.Count);
    }

    private static async Task<string> writeMigrationScriptAsync(Table table)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);
        var migration = await SchemaMigration.DetermineAsync(conn, ct, table);
        using var writer = new StringWriter();
        migration.WriteAllUpdates(writer, new MySqlMigrator(), AutoCreate.CreateOrUpdate);
        return writer.ToString();
    }

    private static void assertLegacyMigrationScript(string script)
    {
        script.ToLowerInvariant().ShouldNotContain(QueueOrdering.QueueOrderColumn);
    }

    private static void assertOptInMigrationScript(string script, Table table)
    {
        var uniqueKey = QueueOrdering.UniqueKeyName(table.Identifier.Name);

        // The column and the key it requires go in together, which numbers the existing rows
        script.ShouldContain(
            $"ALTER TABLE {table.Identifier.QualifiedName} ADD COLUMN `queue_order` BIGINT NOT NULL AUTO_INCREMENT, ADD UNIQUE KEY `{uniqueKey}` (`queue_order`);");

        var composite = table.Indexes.Single(x => x.Columns.Length == 2 && x.Columns[1] == QueueOrdering.QueueOrderColumn);
        script.ShouldContain($"CREATE INDEX `{composite.Name}`");

        var normalized = script.ToLowerInvariant();
        normalized.ShouldNotContain("drop table");
        normalized.ShouldNotContain("drop index");
        normalized.ShouldNotContain("drop column");
        normalized.ShouldNotContain("create table");
        normalized.ShouldNotContain("create unique index");
    }

    private static async Task assertLegacyMessagesAndIndexesAsync(Table table, IReadOnlyList<Guid> ids)
    {
        var schema = table.Identifier.Schema!;
        var tableName = table.Identifier.Name;
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);

        await using (var rows = conn.CreateCommand())
        {
            rows.CommandText = $"SELECT id, body FROM {schema}.{tableName}";
            await using var reader = await rows.ExecuteReaderAsync(ct);
            var actual = new Dictionary<Guid, byte[]>();
            while (await reader.ReadAsync(ct))
            {
                actual.Add(reader.GetGuid(0), (byte[])reader.GetValue(1));
            }

            actual.Count.ShouldBe(2);
            actual.Keys.ShouldBe(ids, ignoreOrder: true);
            actual[ids[0]].ShouldBe(new byte[] { 1 });
            actual[ids[1]].ShouldBe(new byte[] { 2 });
        }

        await queue_ordering_is_opt_in.assertQueueSchemaAsync(table, ordered: false);
    }

    private static async Task stopAndDropAsync(IHost? host, string schema)
    {
        if (host != null)
        {
            await host.StopAsync(ct);
            host.Dispose();
        }

        await executeAsync($"DROP DATABASE IF EXISTS {schema}");
    }

    private static async Task executeAsync(string sql, IReadOnlyList<Guid>? ids = null)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (ids != null)
        {
            cmd.Parameters.AddWithValue("@id1", ids[0]);
            cmd.Parameters.AddWithValue("@id2", ids[1]);
            cmd.Parameters.AddWithValue("@body1", new byte[] { 1 });
            cmd.Parameters.AddWithValue("@body2", new byte[] { 2 });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
