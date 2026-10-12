using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-4862. Existing ready and scheduled queue tables must gain the ordering column and composite
/// index through the resource migration path without discarding messages that were already queued.
/// </summary>
public class QueueOrderSchemaMigrations : PostgresqlContext
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadyQueueUpgradePreservesExistingMessages()
    {
        var schema = $"queue_order_{Guid.NewGuid():N}";
        const string queueName = "ready_upgrade";
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await dropSchemaAsync(schema);
        IHost? host = null;

        try
        {
            host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, schema,
                        transportSchema: schema);
                    opts.ListenToPostgresqlQueue(queueName).PollingInterval(1.Hours());
                }).StartAsync(ct);

            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var transport = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
            var queue = transport.Queues[queueName];
            await runtime.Endpoints.StopListenerAsync(queue, ct);

            var table = queue.QueueTable.Identifier;
            var index = queue.QueueTable.Indexes.Single().Name;

            // Recreate the pre-GH-4862 shape after initial provisioning: populated table,
            // no queue_order column, and the old single-column index with its existing name.
            await createLegacyReadyQueueAsync(table.Schema!, table.Name, index, ids);

            await queue.SetupAsync(NullLogger.Instance);

            await assertMessagesAndOrderingAsync(table.Schema!, table.Name, index, "timestamp", ids);
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
    public async Task ScheduledQueueUpgradePreservesExistingMessages()
    {
        var schema = $"queue_order_{Guid.NewGuid():N}";
        const string queueName = "scheduled_upgrade";
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await dropSchemaAsync(schema);
        IHost? host = null;

        try
        {
            host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, schema,
                        transportSchema: schema);
                    opts.ListenToPostgresqlQueue(queueName).PollingInterval(1.Hours());
                }).StartAsync(ct);

            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var transport = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
            var queue = transport.Queues[queueName];
            await runtime.Endpoints.StopListenerAsync(queue, ct);

            var table = queue.ScheduledTable.Identifier;
            var index = queue.ScheduledTable.Indexes.Single().Name;

            // Recreate the pre-GH-4862 shape with scheduled messages already present. A far-future
            // execution time keeps the scheduler from promoting these rows during the test.
            await createLegacyScheduledQueueAsync(table.Schema!, table.Name, index, ids);

            await queue.SetupAsync(NullLogger.Instance);

            await assertMessagesAndOrderingAsync(table.Schema!, table.Name, index, "execution_time", ids);
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

    private static async Task createLegacyReadyQueueAsync(string schema, string table, string index,
        IReadOnlyList<Guid> ids)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
                          DROP INDEX "{schema}"."{index}";
                          ALTER TABLE "{schema}"."{table}" DROP COLUMN queue_order;
                          CREATE INDEX "{index}" ON "{schema}"."{table}" (timestamp);
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

    private static async Task createLegacyScheduledQueueAsync(string schema, string table, string index,
        IReadOnlyList<Guid> ids)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
                          DROP INDEX "{schema}"."{index}";
                          ALTER TABLE "{schema}"."{table}" DROP COLUMN queue_order;
                          CREATE INDEX "{index}" ON "{schema}"."{table}" (execution_time);
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

    private static async Task assertMessagesAndOrderingAsync(string schema, string table, string index,
        string leadingIndexColumn, IReadOnlyList<Guid> ids)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using (var rows = conn.CreateCommand())
        {
            rows.CommandText = $"""
                               SELECT count(*), count(queue_order), count(DISTINCT queue_order), count(DISTINCT id)
                               FROM "{schema}"."{table}";
                               """;
            await using var reader = await rows.ExecuteReaderAsync(ct);
            (await reader.ReadAsync(ct)).ShouldBeTrue();
            reader.GetInt64(0).ShouldBe(2L);
            reader.GetInt64(1).ShouldBe(2L);
            reader.GetInt64(2).ShouldBe(2L);
            reader.GetInt64(3).ShouldBe(2L);
        }

        await using (var savedIds = conn.CreateCommand())
        {
            savedIds.CommandText = $"SELECT id FROM \"{schema}\".\"{table}\"";
            await using var reader = await savedIds.ExecuteReaderAsync(ct);
            var actual = new List<Guid>();
            while (await reader.ReadAsync(ct)) actual.Add(reader.GetGuid(0));
            actual.ShouldBe(ids, ignoreOrder: true);
        }

        await using (var indexDefinition = conn.CreateCommand())
        {
            indexDefinition.CommandText = """
                                           SELECT indexdef FROM pg_indexes
                                           WHERE schemaname = @schema AND tablename = @table AND indexname = @index;
                                           """;
            indexDefinition.Parameters.AddWithValue("schema", schema);
            indexDefinition.Parameters.AddWithValue("table", table);
            indexDefinition.Parameters.AddWithValue("index", index);
            var definition = (string?)await indexDefinition.ExecuteScalarAsync(ct);
            definition.ShouldNotBeNull();
            definition.Replace("\"", string.Empty, StringComparison.Ordinal)
                .Contains($"({leadingIndexColumn}, queue_order)", StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue($"Expected the upgraded index to cover {leadingIndexColumn} and queue_order, but found: {definition}");
        }
    }

    private static async Task dropSchemaAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await conn.DropSchemaAsync(schema, ct: ct);
    }
}
