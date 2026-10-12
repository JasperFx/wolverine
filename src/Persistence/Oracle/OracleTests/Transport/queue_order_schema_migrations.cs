using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Core;
using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Oracle.Util;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Xunit;

namespace OracleTests.Transport;

/// <summary>
/// GH-4862. The opt-in ordering column and indexes must be added to populated legacy queue tables
/// through both explicit queue setup and the message store's normal resource migration path.
/// </summary>
public class queue_order_schema_migrations : OracleContext
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    private readonly ITestOutputHelper _output;

    public queue_order_schema_migrations(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task disabled_resource_migrations_leave_populated_legacy_queue_tables_unchanged()
    {
        var queueName = newQueueName();
        var readyIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var scheduledIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        IHost? host = null;

        try
        {
            host = await startHostAsync(queueName, deterministicOrdering: false);
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var queue = runtime.Options.Transports.GetOrCreate<OracleTransport>().Queues[queueName];
            await runtime.Endpoints.StopListenerAsync(queue, ct);

            await insertRowsAsync(queue.QueueTable, readyIds, scheduled: false);
            await insertRowsAsync(queue.ScheduledTable, scheduledIds, scheduled: true);

            assertLegacyMigrationScript(await writeMigrationScriptAsync(queue.QueueTable));
            assertLegacyMigrationScript(await writeMigrationScriptAsync(queue.ScheduledTable));

            await runtime.Storage.Admin.MigrateAsync();

            await assertMessagesAsync(queue.QueueTable, readyIds, ordered: false);
            await assertMessagesAsync(queue.ScheduledTable, scheduledIds, ordered: false);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(queue.QueueTable, ordered: false);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(queue.ScheduledTable, ordered: false);
            (await queue.CheckAsync()).ShouldBeTrue();

            await queue.SetupAsync(NullLogger.Instance);
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await queue_ordering_is_opt_in.stopAndDropAsync(host, queueName);
        }
    }

    [Fact]
    public async Task ready_queue_upgrade_preserves_existing_messages()
    {
        var queueName = newQueueName();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        IHost? host = null;

        try
        {
            host = await startHostAsync(queueName);
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var queue = runtime.Options.Transports.GetOrCreate<OracleTransport>().Queues[queueName];
            await runtime.Endpoints.StopListenerAsync(queue, ct);

            var table = queue.QueueTable;
            await revertToLegacyShapeAsync(table);
            await insertRowsAsync(table, ids, scheduled: false);

            assertOptInMigrationScript(await writeMigrationScriptAsync(table), table.Indexes.Last().Name);
            await queue.SetupAsync(NullLogger.Instance);

            await assertMessagesAsync(table, ids, ordered: true);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(table, ordered: true);
            (await queue.CheckAsync()).ShouldBeTrue();

            // Explicit setup is idempotent after the upgrade
            (await writeMigrationScriptAsync(table)).ShouldBeEmpty();
            await queue.SetupAsync(NullLogger.Instance);
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await queue_ordering_is_opt_in.stopAndDropAsync(host, queueName);
        }
    }

    [Fact]
    public async Task scheduled_queue_upgrade_uses_message_store_resource_migration()
    {
        var queueName = newQueueName();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        IHost? host = null;

        try
        {
            host = await startHostAsync(queueName);
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var queue = runtime.Options.Transports.GetOrCreate<OracleTransport>().Queues[queueName];
            await runtime.Endpoints.StopListenerAsync(queue, ct);

            var table = queue.ScheduledTable;
            await revertToLegacyShapeAsync(table);
            await insertRowsAsync(table, ids, scheduled: true);

            assertOptInMigrationScript(await writeMigrationScriptAsync(table), table.Indexes.Last().Name);

            // The queue tables are registered with the OracleMessageStore. Migrate through the admin
            // entry point to cover the ordinary startup/administrative resource path too.
            await runtime.Storage.Admin.MigrateAsync();

            await assertMessagesAsync(table, ids, ordered: true);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(table, ordered: true);
            (await queue.CheckAsync()).ShouldBeTrue();

            await runtime.Storage.Admin.MigrateAsync();
            (await writeMigrationScriptAsync(table)).ShouldBeEmpty();
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await queue_ordering_is_opt_in.stopAndDropAsync(host, queueName);
        }
    }

    private static string newQueueName() => $"QM_{Guid.NewGuid().ToString("N")[..10]}".ToUpperInvariant();

    private static async Task<IHost> startHostAsync(string queueName, bool deterministicOrdering = true)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .EnableMessageTransport(x =>
                    {
                        if (deterministicOrdering)
                        {
                            x.EnableDeterministicQueueOrdering();
                        }
                    });

                opts.ListenToOracleQueue(queueName).PollingInterval(1.Hours());
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();
            }).StartAsync(ct);
    }

    // Puts an opted-in table back into the shape every release before GH-4862 created
    private static async Task revertToLegacyShapeAsync(Table table)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(ct);

        var compositeIndex = table.Indexes.Last();
        compositeIndex.Columns!.ShouldContain(QueueTable.QueueOrderColumn);

        // A poll that was in flight when the listener stopped can still hold its table lock for a
        // moment, and DDL fails immediately with ORA-00054 by default
        await using (var wait = conn.CreateCommand("ALTER SESSION SET DDL_LOCK_TIMEOUT = 30"))
        {
            await wait.ExecuteNonQueryAsync(ct);
        }

        await using (var dropIndex = conn.CreateCommand($"DROP INDEX {table.Identifier.Schema}.{compositeIndex.Name}"))
        {
            await dropIndex.ExecuteNonQueryAsync(ct);
        }

        await using (var dropColumn = conn.CreateCommand(
                         $"ALTER TABLE {table.Identifier.QualifiedName} DROP COLUMN {QueueTable.QueueOrderColumn}"))
        {
            await dropColumn.ExecuteNonQueryAsync(ct);
        }

        await queue_ordering_is_opt_in.assertQueueSchemaAsync(table, ordered: false);
    }

    private static async Task insertRowsAsync(Table table, IReadOnlyList<Guid> ids, bool scheduled)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(ct);

        for (var i = 0; i < ids.Count; i++)
        {
            // Every row shares one timestamp, as rows written within one statement can
            var sql = scheduled
                ? $"INSERT INTO {table.Identifier.QualifiedName} (id, body, message_type, execution_time, timestamp) VALUES (:id, :body, 'upgrade-test', :executionTime, :timestamp)"
                : $"INSERT INTO {table.Identifier.QualifiedName} (id, body, message_type, timestamp) VALUES (:id, :body, 'upgrade-test', :timestamp)";

            await using var cmd = conn.CreateCommand(sql);
            cmd.With("id", ids[i]);
            cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = new[] { (byte)(i + 1) } });
            if (scheduled)
            {
                cmd.Parameters.Add(new OracleParameter("executionTime", OracleDbType.TimeStampTZ)
                {
                    Value = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)
                });
            }

            cmd.Parameters.Add(new OracleParameter("timestamp", OracleDbType.TimeStampTZ)
            {
                Value = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task assertMessagesAsync(Table table, IReadOnlyList<Guid> ids, bool ordered)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(ct);

        var columns = ordered ? $"id, body, {QueueTable.QueueOrderColumn}" : "id, body";
        await using var rows = conn.CreateCommand($"SELECT {columns} FROM {table.Identifier.QualifiedName}");
        await using var reader = await rows.ExecuteReaderAsync(ct);

        var bodies = new Dictionary<Guid, byte[]>();
        var queueOrders = new List<long>();
        while (await reader.ReadAsync(ct))
        {
            var id = await OracleEnvelopeReader.ReadGuidAsync(reader, 0, ct);
            bodies.Add(id, (byte[])reader.GetValue(1));
            if (ordered)
            {
                queueOrders.Add(Convert.ToInt64(reader.GetValue(2)));
            }
        }

        bodies.Keys.ShouldBe(ids, ignoreOrder: true);
        for (var i = 0; i < ids.Count; i++)
        {
            bodies[ids[i]].ShouldBe(new[] { (byte)(i + 1) });
        }

        if (ordered)
        {
            // Oracle numbers every existing row when the identity column is added
            queueOrders.Distinct().Count().ShouldBe(ids.Count);
        }
    }

    private async Task<string> writeMigrationScriptAsync(Table table)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(ct);
        // Oracle cannot run Weasel's batched multi-table determination, so use the per-table delta
        // that OracleQueue.CheckAsync() uses
        var migration = new SchemaMigration(await table.FindDeltaAsync(conn, ct));
        await using var writer = new StringWriter();
        migration.WriteAllUpdates(writer, new OracleMigrator(), AutoCreate.CreateOrUpdate);
        var script = writer.ToString().Trim();
        _output.WriteLine($"Migration for {table.Identifier}:{Environment.NewLine}{script}");
        return script;
    }

    private static void assertLegacyMigrationScript(string script)
    {
        var normalized = script.ToLowerInvariant();
        normalized.ShouldNotContain(QueueTable.QueueOrderColumn);
        normalized.ShouldNotContain("_ts_order");
        normalized.ShouldNotContain("_exec_order");
    }

    private static void assertOptInMigrationScript(string script, string compositeIndexName)
    {
        var normalized = script.ToLowerInvariant();
        normalized.ShouldContain($"add {QueueTable.QueueOrderColumn} number(19) generated by default as identity");
        normalized.ShouldContain(compositeIndexName.ToLowerInvariant());
        normalized.ShouldNotContain("drop table");
        normalized.ShouldNotContain("drop index");
        normalized.ShouldNotContain("drop column");
        normalized.ShouldNotContain("create table");
    }
}
