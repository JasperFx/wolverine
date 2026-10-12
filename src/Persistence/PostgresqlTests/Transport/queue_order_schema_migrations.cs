using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-4862. The opt-in ordering columns and indexes must be added to populated legacy queue tables
/// through both explicit queue setup and the message store's normal resource migration path.
/// </summary>
public class queue_order_schema_migrations : PostgresqlContext
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task disabled_resource_migrations_leave_populated_legacy_queue_tables_unchanged()
    {
        var schema = $"queue_order_{Guid.NewGuid():N}";
        const string queueName = "legacy_without_opt_in";
        var readyIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var scheduledIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await dropSchemaAsync(schema);
        IHost? host = null;

        try
        {
            host = await startHostAsync(schema, queueName, deterministicOrdering: false);
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues[queueName];
            await runtime.Endpoints.StopListenerAsync(queue, ct);

            var readyTable = queue.QueueTable;
            var scheduledTable = queue.ScheduledTable;
            await createLegacyReadyQueueAsync(readyTable.Identifier.Schema!, readyTable.Identifier.Name,
                readyTable.Indexes.Select(x => x.Name), readyTable.Indexes[0].Name, readyIds);
            await createLegacyScheduledQueueAsync(scheduledTable.Identifier.Schema!, scheduledTable.Identifier.Name,
                scheduledTable.Indexes.Select(x => x.Name), scheduledTable.Indexes[0].Name, scheduledIds);

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
            if (host != null)
            {
                await host.StopAsync(ct);
                host.Dispose();
            }

            await dropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ready_queue_upgrade_preserves_existing_messages()
    {
        var schema = $"queue_order_{Guid.NewGuid():N}";
        const string queueName = "ready_upgrade";
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await dropSchemaAsync(schema);
        IHost? host = null;

        try
        {
            host = await startHostAsync(schema, queueName);
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues[queueName];
            await runtime.Endpoints.StopListenerAsync(queue, ct);

            var table = queue.QueueTable;
            var legacyIndex = table.Indexes[0].Name;
            await createLegacyReadyQueueAsync(table.Identifier.Schema!, table.Identifier.Name,
                table.Indexes.Select(x => x.Name), legacyIndex, ids);

            assertOptInMigrationScript(await writeMigrationScriptAsync(table), table.Indexes[1].Name);
            await queue.SetupAsync(NullLogger.Instance);

            await assertMessagesAndIndexesAsync(table, ids);
            (await queue.CheckAsync()).ShouldBeTrue();

            // Explicit setup is idempotent after the upgrade.
            await queue.SetupAsync(NullLogger.Instance);
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            if (host != null)
            {
                await host.StopAsync(ct);
                host.Dispose();
            }

            await dropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task scheduled_queue_upgrade_uses_message_store_resource_migration()
    {
        var schema = $"queue_order_{Guid.NewGuid():N}";
        const string queueName = "scheduled_upgrade";
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await dropSchemaAsync(schema);
        IHost? host = null;

        try
        {
            host = await startHostAsync(schema, queueName);
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues[queueName];
            await runtime.Endpoints.StopListenerAsync(queue, ct);

            var table = queue.ScheduledTable;
            var legacyIndex = table.Indexes[0].Name;
            await createLegacyScheduledQueueAsync(table.Identifier.Schema!, table.Identifier.Name,
                table.Indexes.Select(x => x.Name), legacyIndex, ids);

            assertOptInMigrationScript(await writeMigrationScriptAsync(table), table.Indexes[1].Name);
            // The queue tables are registered with PostgresqlMessageStore.AllObjects(). Migrate through
            // the admin entry point to cover the ordinary startup/administrative resource path too.
            await runtime.Storage.Admin.MigrateAsync();

            await assertMessagesAndIndexesAsync(table, ids);
            (await queue.CheckAsync()).ShouldBeTrue();
            await runtime.Storage.Admin.MigrateAsync();
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            if (host != null)
            {
                await host.StopAsync(ct);
                host.Dispose();
            }

            await dropSchemaAsync(schema);
        }
    }

    private static async Task<IHost> startHostAsync(string schema, string queueName, bool deterministicOrdering = true)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var expression = opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, schema,
                    transportSchema: schema);
                if (deterministicOrdering) expression.EnableDeterministicQueueOrdering();

                opts.ListenToPostgresqlQueue(queueName).PollingInterval(1.Hours());
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();
            }).StartAsync(ct);
    }

    private static async Task createLegacyReadyQueueAsync(string schema, string table,
        IEnumerable<string> indexesToDrop, string legacyIndex, IReadOnlyList<Guid> ids)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        var dropIndexes = string.Join(Environment.NewLine,
            indexesToDrop.Select(index => $"DROP INDEX IF EXISTS \"{schema}\".\"{index}\";"));
        cmd.CommandText = $"""
                          {dropIndexes}
                          ALTER TABLE "{schema}"."{table}" DROP COLUMN IF EXISTS queue_order;
                          CREATE INDEX "{legacyIndex}" ON "{schema}"."{table}" (timestamp);
                          INSERT INTO "{schema}"."{table}" (id, body, message_type, timestamp)
                          VALUES (@id1, @body1, 'upgrade-test', '2026-01-01T00:00:00Z'),
                                 (@id2, @body2, 'upgrade-test', '2026-01-01T00:00:00Z');
                          """;
        cmd.Parameters.AddWithValue("id1", ids[0]);
        cmd.Parameters.AddWithValue("id2", ids[1]);
        cmd.Parameters.AddWithValue("body1", new byte[] { 1 });
        cmd.Parameters.AddWithValue("body2", new byte[] { 2 });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task createLegacyScheduledQueueAsync(string schema, string table,
        IEnumerable<string> indexesToDrop, string legacyIndex, IReadOnlyList<Guid> ids)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        var dropIndexes = string.Join(Environment.NewLine,
            indexesToDrop.Select(index => $"DROP INDEX IF EXISTS \"{schema}\".\"{index}\";"));
        cmd.CommandText = $"""
                          {dropIndexes}
                          ALTER TABLE "{schema}"."{table}" DROP COLUMN IF EXISTS queue_order;
                          CREATE INDEX "{legacyIndex}" ON "{schema}"."{table}" (execution_time);
                          INSERT INTO "{schema}"."{table}" (id, body, message_type, execution_time, timestamp)
                          VALUES (@id1, @body1, 'upgrade-test', '2100-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                                 (@id2, @body2, 'upgrade-test', '2100-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                          """;
        cmd.Parameters.AddWithValue("id1", ids[0]);
        cmd.Parameters.AddWithValue("id2", ids[1]);
        cmd.Parameters.AddWithValue("body1", new byte[] { 1 });
        cmd.Parameters.AddWithValue("body2", new byte[] { 2 });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task assertMessagesAndIndexesAsync(Table table, IReadOnlyList<Guid> ids)
    {
        var schema = table.Identifier.Schema!;
        var tableName = table.Identifier.Name;
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using (var rows = conn.CreateCommand())
        {
            rows.CommandText = $"""
                               SELECT id, body, queue_order
                               FROM "{schema}"."{tableName}";
                               """;
            await using var reader = await rows.ExecuteReaderAsync(ct);
            var actual = new Dictionary<Guid, (byte[] Body, long QueueOrder)>();
            while (await reader.ReadAsync(ct))
            {
                var body = await reader.GetFieldValueAsync<byte[]>(1, ct);
                actual.Add(reader.GetGuid(0), (body, reader.GetInt64(2)));
            }

            actual.Count.ShouldBe(2);
            actual.Keys.ShouldBe(ids, ignoreOrder: true);
            actual[ids[0]].Body.ShouldBe(new byte[] { 1 });
            actual[ids[1]].Body.ShouldBe(new byte[] { 2 });
            actual.Values.Select(x => x.QueueOrder).Distinct().Count().ShouldBe(2);
        }

        foreach (var index in table.Indexes)
        {
            await using var indexDefinition = conn.CreateCommand();
            indexDefinition.CommandText = """
                                           SELECT indexdef FROM pg_indexes
                                           WHERE schemaname = @schema AND tablename = @table AND indexname = @index;
                                           """;
            indexDefinition.Parameters.AddWithValue("schema", schema);
            indexDefinition.Parameters.AddWithValue("table", tableName);
            indexDefinition.Parameters.AddWithValue("index", index.Name);
            var definition = (string?)await indexDefinition.ExecuteScalarAsync(ct);
            definition.ShouldNotBeNull();
            var expectedColumns = $"({string.Join(", ", index.Columns!)})";
            definition.Replace("\"", string.Empty, StringComparison.Ordinal)
                .Contains(expectedColumns, StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue($"Expected index {index.Name} to cover {expectedColumns}, but found: {definition}");
        }
    }

    private static async Task<string> writeMigrationScriptAsync(Table table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        var migration = await SchemaMigration.DetermineAsync(conn, ct, table);
        using var writer = new StringWriter();
        migration.WriteAllUpdates(writer, new PostgresqlMigrator(), AutoCreate.CreateOrUpdate);
        return writer.ToString();
    }

    private static void assertLegacyMigrationScript(string script)
    {
        var normalized = script.ToLowerInvariant();
        normalized.ShouldNotContain(QueueTable.QueueOrderColumn);
        normalized.ShouldNotContain("timestamp_queue_order");
        normalized.ShouldNotContain("execution_time_queue_order");
    }

    private static void assertOptInMigrationScript(string script, string compositeIndexName)
    {
        var normalized = script.ToLowerInvariant();
        normalized.ShouldContain("add column if not exists");
        normalized.ShouldContain(QueueTable.QueueOrderColumn);
        normalized.ShouldContain(compositeIndexName.ToLowerInvariant());
        normalized.ShouldNotContain("drop table");
        normalized.ShouldNotContain("drop index");
        normalized.ShouldNotContain("drop column");
        normalized.ShouldNotContain("create table");
    }

    private static async Task assertLegacyMessagesAndIndexesAsync(Table table, IReadOnlyList<Guid> ids)
    {
        var schema = table.Identifier.Schema!;
        var tableName = table.Identifier.Name;
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using (var rows = conn.CreateCommand())
        {
            rows.CommandText = $"SELECT id, body FROM \"{schema}\".\"{tableName}\";";
            await using var reader = await rows.ExecuteReaderAsync(ct);
            var actual = new Dictionary<Guid, byte[]>();
            while (await reader.ReadAsync(ct))
            {
                actual.Add(reader.GetGuid(0), await reader.GetFieldValueAsync<byte[]>(1, ct));
            }

            actual.Count.ShouldBe(2);
            actual.Keys.ShouldBe(ids, ignoreOrder: true);
            actual[ids[0]].ShouldBe(new byte[] { 1 });
            actual[ids[1]].ShouldBe(new byte[] { 2 });
        }

        await using var schemaShape = conn.CreateCommand();
        schemaShape.CommandText = """
                                  SELECT
                                      (SELECT count(*) FROM information_schema.columns
                                       WHERE table_schema = @schema AND table_name = @table AND column_name = 'queue_order'),
                                      (SELECT count(*) FROM pg_indexes
                                       WHERE schemaname = @schema AND tablename = @table AND indexname LIKE 'idx_%');
                                  """;
        schemaShape.Parameters.AddWithValue("schema", schema);
        schemaShape.Parameters.AddWithValue("table", tableName);
        await using var shapeReader = await schemaShape.ExecuteReaderAsync(ct);
        (await shapeReader.ReadAsync(ct)).ShouldBeTrue();
        shapeReader.GetInt64(0).ShouldBe(0L);
        shapeReader.GetInt64(1).ShouldBe(1L);
    }

    private static async Task dropSchemaAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await conn.DropSchemaAsync(schema, ct: ct);
    }
}
