using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;

namespace SqlServerTests.Transport;

/// <summary>
/// GH-4862. The opt-in queue_order identity and its indexes must be added to populated legacy queue
/// tables in place, through both explicit queue setup and the transport's resource setup path,
/// with every existing row numbered.
/// </summary>
public class queue_order_schema_migrations
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task disabled_resource_migrations_leave_populated_legacy_queue_tables_unchanged()
    {
        var schema = newSchema();
        var readyIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var scheduledIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        IHost? host = null;

        try
        {
            host = await startHostAsync(schema, "legacy_without_opt_in", deterministicOrdering: false);
            var (queue, resource) = await queueAndResourceAsync(host, "legacy_without_opt_in");

            await insertRowsAsync(queue.QueueTable, readyIds, scheduled: false);
            await insertRowsAsync(queue.ScheduledTable, scheduledIds, scheduled: true);

            assertLegacyMigrationScript(await writeMigrationScriptAsync(queue.QueueTable));
            assertLegacyMigrationScript(await writeMigrationScriptAsync(queue.ScheduledTable));

            await resource.Setup(ct);
            await queue.SetupAsync(NullLogger.Instance);

            await assertMessagesAsync(queue.QueueTable, readyIds, ordered: false);
            await assertMessagesAsync(queue.ScheduledTable, scheduledIds, ordered: false);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(queue.QueueTable, ordered: false);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(queue.ScheduledTable, ordered: false);
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
        var schema = newSchema();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        IHost? host = null;

        try
        {
            host = await startHostAsync(schema, "ready_upgrade");
            var (queue, _) = await queueAndResourceAsync(host, "ready_upgrade");

            var table = queue.QueueTable;
            await revertToLegacyLayoutAsync(table);
            await insertRowsAsync(table, ids, scheduled: false);

            assertOptInMigrationScript(await writeMigrationScriptAsync(table), table.Indexes[1].Name);
            await queue.SetupAsync(NullLogger.Instance);

            await assertMessagesAsync(table, ids, ordered: true);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(table, ordered: true);
            (await queue.CheckAsync()).ShouldBeTrue();

            // Explicit setup is idempotent after the upgrade.
            await queue.SetupAsync(NullLogger.Instance);
            (await writeMigrationScriptAsync(table)).Trim().ShouldBeEmpty();
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Fact]
    public async Task scheduled_queue_upgrade_uses_the_transport_resource_migration()
    {
        var schema = newSchema();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        IHost? host = null;

        try
        {
            host = await startHostAsync(schema, "scheduled_upgrade");
            var (queue, resource) = await queueAndResourceAsync(host, "scheduled_upgrade");

            var table = queue.ScheduledTable;
            await revertToLegacyLayoutAsync(table);
            await insertRowsAsync(table, ids, scheduled: true);

            assertOptInMigrationScript(await writeMigrationScriptAsync(table), table.Indexes[1].Name);

            // The queue tables are registered through SqlServerTransportDatabase's feature schemas, the
            // ordinary startup/administrative resource path.
            await resource.Setup(ct);

            await assertMessagesAsync(table, ids, ordered: true);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(table, ordered: true);
            (await queue.CheckAsync()).ShouldBeTrue();

            await resource.Setup(ct);
            (await writeMigrationScriptAsync(table)).Trim().ShouldBeEmpty();
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    private static string newSchema() => $"queue_order_{Guid.NewGuid():N}";

    private static async Task<IHost> startHostAsync(string schema, string queueName, bool deterministicOrdering = true)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var expression = opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, schema,
                    transportSchema: schema).AutoProvision();
                if (deterministicOrdering) expression.EnableDeterministicQueueOrdering();

                opts.ListenToSqlServerQueue(queueName).PollingInterval(1.Hours());
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();
            }).StartAsync(ct);
    }

    private static async Task<(SqlServerQueue, IStatefulResource)> queueAndResourceAsync(IHost host, string queueName)
    {
        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.OfType<SqlServerTransport>().Single();
        var queue = transport.Queues[queueName];
        await runtime.Endpoints.StopListenerAsync(queue, ct);

        transport.TryBuildStatefulResource(runtime, out var resource).ShouldBeTrue();
        return (queue, resource!);
    }

    /// <summary>
    /// Put an opted-in table back into the shape released before GH-4862: no queue_order column and
    /// no composite index, just the legacy single-column index.
    /// </summary>
    private static async Task revertToLegacyLayoutAsync(Table table)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);

        var composite = table.Indexes.Single(x => x.Name.EndsWith("_queue_order"));
        await conn.CreateCommand($"""
                                  DROP INDEX [{composite.Name}] ON {table.Identifier};
                                  ALTER TABLE {table.Identifier} DROP COLUMN {QueueTable.QueueOrderColumn};
                                  """).ExecuteNonQueryAsync(ct);

        await queue_ordering_is_opt_in.assertQueueSchemaAsync(withoutOptIn(table), ordered: false);
    }

    private static Table withoutOptIn(Table table)
    {
        var legacy = new Table(table.Identifier);
        foreach (var index in table.Indexes.Where(x => !x.Name.EndsWith("_queue_order"))) legacy.Indexes.Add(index);
        return legacy;
    }

    private static async Task insertRowsAsync(Table table, IReadOnlyList<Guid> ids, bool scheduled)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);

        // One multi-row INSERT, so every row shares one SYSDATETIMEOFFSET() -- the tie the column breaks
        var values = ids.Select((_, i) => scheduled
            ? $"(@id{i}, @body{i}, 'upgrade-test', '2100-01-01T00:00:00Z')"
            : $"(@id{i}, @body{i}, 'upgrade-test')").Join(", ");
        var columns = scheduled ? "id, body, message_type, execution_time" : "id, body, message_type";
        var cmd = conn.CreateCommand($"INSERT INTO {table.Identifier} ({columns}) VALUES {values}");
        for (var i = 0; i < ids.Count; i++)
        {
            cmd.With($"id{i}", ids[i]);
            cmd.With($"body{i}", new[] { (byte)(i + 1) });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task assertMessagesAsync(Table table, IReadOnlyList<Guid> ids, bool ordered)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);

        var select = ordered ? "id, body, queue_order" : "id, body";
        var actual = new Dictionary<Guid, (byte[] Body, long QueueOrder)>();
        await using (var reader = await conn.CreateCommand($"SELECT {select} FROM {table.Identifier}")
                         .ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var body = await reader.GetFieldValueAsync<byte[]>(1, ct);
                actual.Add(reader.GetGuid(0), (body, ordered ? reader.GetInt64(2) : 0));
            }
        }

        actual.Keys.ShouldBe(ids, ignoreOrder: true);
        for (var i = 0; i < ids.Count; i++)
        {
            actual[ids[i]].Body.ShouldBe(new[] { (byte)(i + 1) });
        }

        if (ordered)
        {
            actual.Values.Select(x => x.QueueOrder).Distinct().Count().ShouldBe(ids.Count);
        }
    }

    private static async Task<string> writeMigrationScriptAsync(Table table)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);
        var migration = await SchemaMigration.DetermineAsync(conn, ct, table);
        await using var writer = new StringWriter();
        migration.WriteAllUpdates(writer, new SqlServerMigrator(), AutoCreate.CreateOrUpdate);
        return writer.ToString();
    }

    private static void assertLegacyMigrationScript(string script)
    {
        var normalized = script.ToLowerInvariant();
        normalized.ShouldNotContain(QueueTable.QueueOrderColumn);
        normalized.ShouldNotContain("drop table");
        normalized.ShouldNotContain("create table");
    }

    private static void assertOptInMigrationScript(string script, string compositeIndexName)
    {
        var normalized = script.ToLowerInvariant();
        normalized.ShouldContain("bigint not null identity");
        normalized.ShouldContain($"if not exists (select 1 from sys.columns");
        normalized.ShouldContain(compositeIndexName.ToLowerInvariant());
        normalized.ShouldNotContain("drop table");
        normalized.ShouldNotContain("drop index");
        normalized.ShouldNotContain("drop column");
        normalized.ShouldNotContain("create table");
    }

    private static async Task stopAndDropAsync(IHost? host, string schema)
    {
        if (host != null)
        {
            await host.StopAsync(ct);
            host.Dispose();
        }

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);
        await conn.DropSchemaAsync(schema);
    }
}
