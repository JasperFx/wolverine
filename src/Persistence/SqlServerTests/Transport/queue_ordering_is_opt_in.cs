using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;

namespace SqlServerTests.Transport;

/// <summary>
/// GH-4862. Pops hand their batch over in queue order whatever DELETE ... OUTPUT returns, and the
/// opt-in queue_order identity breaks timestamp ties. Queues on the OptimizeQueueThroughput layout
/// order by their seq identity and never get the extra column.
/// </summary>
public class queue_ordering_is_opt_in
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task default_configuration_keeps_the_legacy_queue_schema(bool optimizeThroughput)
    {
        var schema = newSchema();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: false, optimizeThroughput: optimizeThroughput);
            host = started.Host;

            await assertQueueSchemaAsync(started.Queue.QueueTable, ordered: false);
            await assertQueueSchemaAsync(started.Queue.ScheduledTable, ordered: false);
            (await started.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Fact]
    public async Task ordering_can_be_enabled_through_the_persistence_expression()
    {
        var schema = newSchema();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: true);
            host = started.Host;

            started.Queue.UsesQueueOrderColumn.ShouldBeTrue();
            await assertQueueSchemaAsync(started.Queue.QueueTable, ordered: true);
            await assertQueueSchemaAsync(started.Queue.ScheduledTable, ordered: true);
            (await started.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Fact]
    public async Task the_high_throughput_layout_uses_seq_and_gets_no_extra_column()
    {
        var schema = newSchema();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: true, optimizeThroughput: true);
            host = started.Host;

            started.Queue.UsesQueueOrderColumn.ShouldBeFalse();
            await assertQueueSchemaAsync(started.Queue.QueueTable, ordered: false);
            await assertQueueSchemaAsync(started.Queue.ScheduledTable, ordered: false);
            (await started.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task timestamp_order_works_without_the_optional_column(bool buffered)
    {
        var schema = newSchema();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: false, buffered: buffered);
            host = started.Host;
            var queue = started.Queue;

            var envelopes = await sendAsync(queue, 10);

            // Reverse the timestamp order relative to insert order (and so relative to the clustered
            // index and any OUTPUT order) to prove the pop reapplies timestamp ordering to the rows
            // DELETE ... OUTPUT hands back, without needing queue_order in this schema.
            await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
            {
                await conn.OpenAsync(ct);
                for (var i = 0; i < envelopes.Count; i++)
                {
                    await conn.CreateCommand($"UPDATE {queue.QueueTable.Identifier} SET timestamp = @timestamp WHERE id = @id")
                        .With("id", envelopes[i].Id)
                        .With("timestamp", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                            .AddTicks(envelopes.Count - i))
                        .ExecuteNonQueryAsync(ct);
                }
            }

            var received = await popAsync(host, queue, envelopes.Count, buffered);
            received.Select(x => x.Id).ToArray()
                .ShouldBe(envelopes.Select(x => x.Id).Reverse().ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task tied_timestamps_pop_in_send_order_with_ordering_enabled(bool buffered, bool optimizeThroughput)
    {
        var schema = newSchema();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: true, buffered: buffered,
                optimizeThroughput: optimizeThroughput);
            host = started.Host;
            var queue = started.Queue;

            var envelopes = await sendAsync(queue, 20);

            // Every row now ties on timestamp, which is what a scheduled promotion produces: the
            // SYSDATETIMEOFFSET() default is evaluated once per statement.
            await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
            {
                await conn.OpenAsync(ct);
                await conn.CreateCommand($"UPDATE {queue.QueueTable.Identifier} SET timestamp = @timestamp")
                    .With("timestamp", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
                    .ExecuteNonQueryAsync(ct);
            }

            var received = await popAsync(host, queue, envelopes.Count, buffered);
            received.Select(x => x.Id).ToArray().ShouldBe(envelopes.Select(x => x.Id).ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task scheduled_ties_are_promoted_in_order_across_multiple_batches(bool optimizeThroughput)
    {
        var schema = newSchema();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: true, recoveryBatchSize: 7,
                optimizeThroughput: optimizeThroughput);
            host = started.Host;
            var runtime = host.GetRuntime();
            var queue = started.Queue;
            const int total = 23;
            var envelopes = new List<Envelope>();

            for (var i = 0; i < total; i++)
            {
                var envelope = ObjectMother.Envelope();
                envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
                envelopes.Add(envelope);
                await queue.ScheduleMessageAsync(envelope, ct);
            }

            await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
            {
                await conn.OpenAsync(ct);
                await conn.CreateCommand(
                        $"UPDATE {queue.ScheduledTable.Identifier} SET timestamp = @timestamp, execution_time = @execution_time")
                    .With("timestamp", DateTimeOffset.UtcNow)
                    .With("execution_time", DateTimeOffset.UtcNow.AddMinutes(-1))
                    .ExecuteNonQueryAsync(ct);
            }

            var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
            await using var listener = new SqlServerQueueListener(queue, runtime, receiver);
            var promoted = 0;
            while (promoted < total)
            {
                var count = await listener.MoveScheduledToReadyQueueAsync(ct);
                count.ShouldBe(Math.Min(runtime.DurabilitySettings.RecoveryBatchSize, total - promoted));
                promoted += count;
            }

            (await queue.ScheduledCountAsync()).ShouldBe(0);
            (await queue.CountAsync()).ShouldBe(total);
            var received = await listener.TryPopDurablyAsync(total, runtime.DurabilitySettings,
                NullLogger.Instance, ct);
            received.Select(x => x.Id).ToArray().ShouldBe(envelopes.Select(x => x.Id).ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Fact]
    public async Task disabling_after_upgrade_preserves_the_opted_in_schema_and_still_dequeues()
    {
        var schema = newSchema();
        IHost? optedInHost = null;
        IHost? disabledHost = null;
        try
        {
            var optedIn = await startAsync(schema, deterministicOrdering: true);
            optedInHost = optedIn.Host;
            await optedInHost.StopAsync(ct);
            optedInHost.Dispose();
            optedInHost = null;

            var disabled = await startAsync(schema, deterministicOrdering: false);
            disabledHost = disabled.Host;
            await disabled.Queue.SetupAsync(NullLogger.Instance);

            await assertQueueSchemaAsync(disabled.Queue.QueueTable, ordered: false, tolerateOptedInSchema: true);
            await assertQueueSchemaAsync(disabled.Queue.ScheduledTable, ordered: false, tolerateOptedInSchema: true);
            (await disabled.Queue.CheckAsync()).ShouldBeTrue();

            var envelopes = await sendAsync(disabled.Queue, 3);
            var received = await popAsync(disabledHost, disabled.Queue, envelopes.Count, buffered: false);
            received.Select(x => x.Id).ToArray().ShouldBe(envelopes.Select(x => x.Id).ToArray());
        }
        finally
        {
            await stopAndDropAsync(disabledHost, schema);
            if (optedInHost != null)
            {
                await optedInHost.StopAsync(ct);
                optedInHost.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task auto_create_none_uses_a_preprovisioned_schema_matching_the_ordering_setting(
        bool deterministicOrdering)
    {
        var schema = newSchema();
        IHost? provisionHost = null;
        IHost? noCreateHost = null;
        try
        {
            var provisioned = await startAsync(schema, deterministicOrdering);
            provisionHost = provisioned.Host;
            await provisionHost.StopAsync(ct);
            provisionHost.Dispose();
            provisionHost = null;

            var noCreate = await startAsync(schema, deterministicOrdering, autoCreate: AutoCreate.None);
            noCreateHost = noCreate.Host;
            await assertQueueSchemaAsync(noCreate.Queue.QueueTable, ordered: deterministicOrdering);
            await assertQueueSchemaAsync(noCreate.Queue.ScheduledTable, ordered: deterministicOrdering);
            (await noCreate.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(noCreateHost, schema);
            if (provisionHost != null)
            {
                await provisionHost.StopAsync(ct);
                provisionHost.Dispose();
            }
        }
    }

    private static string newSchema() => $"queue_order_opt_in_{Guid.NewGuid():N}";

    private static async Task<List<Envelope>> sendAsync(SqlServerQueue queue, int count)
    {
        // One INSERT per envelope, so each row gets its own SYSDATETIMEOFFSET() and identity value
        var envelopes = new List<Envelope>();
        for (var i = 0; i < count; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelopes.Add(envelope);
            await queue.SendAsync(envelope, ct);
        }

        (await queue.CountAsync()).ShouldBe(count);
        return envelopes;
    }

    private static async Task<IReadOnlyList<Envelope>> popAsync(IHost host, SqlServerQueue queue, int count,
        bool buffered)
    {
        var runtime = host.GetRuntime();
        var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
        await using var listener = new SqlServerQueueListener(queue, runtime, receiver);
        return buffered
            ? await listener.TryPopAsync(count, NullLogger.Instance, ct)
            : await listener.TryPopDurablyAsync(count, runtime.DurabilitySettings, NullLogger.Instance, ct);
    }

    private static async Task<StartedQueue> startAsync(string schema, bool deterministicOrdering,
        bool buffered = false, int recoveryBatchSize = 100, bool optimizeThroughput = false,
        AutoCreate autoCreate = AutoCreate.CreateOrUpdate)
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(ct);
            await conn.CreateCommand($"IF SCHEMA_ID('{schema}') IS NULL EXEC('CREATE SCHEMA [{schema}]')")
                .ExecuteNonQueryAsync(ct);
        }

        const string queueName = "ordered";
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.AutoBuildMessageStorageOnStartup = autoCreate;
                opts.Durability.RecoveryBatchSize = recoveryBatchSize;
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();

                var expression = opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString,
                    schema, transportSchema: schema);
                if (autoCreate != AutoCreate.None) expression.AutoProvision();

                // The queue endpoint exists before the feature options are set; table construction is
                // lazy and must use the final transport configuration.
                var listener = opts.ListenToSqlServerQueue(queueName).PollingInterval(1.Hours());
                if (buffered) listener.BufferedInMemory();

                if (optimizeThroughput) expression.OptimizeQueueThroughput();
                if (deterministicOrdering) expression.EnableDeterministicQueueOrdering();
            }).StartAsync(ct);

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.OfType<SqlServerTransport>().Single();
        var queue = transport.Queues[queueName];

        // Keep the running listener's poll loop from taking anything the test puts in the table.
        // The tests drive their own SqlServerQueueListener instead.
        await runtime.Endpoints.StopListenerAsync(queue, ct);
        return new StartedQueue(host, queue);
    }

    internal static async Task assertQueueSchemaAsync(Table table, bool ordered, bool tolerateOptedInSchema = false)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);
        var qualifiedName = table.Identifier.QualifiedName;

        var identity = await conn.CreateCommand("""
                                                SELECT count(*) FROM sys.columns
                                                WHERE object_id = OBJECT_ID(@table) AND name = 'queue_order' AND is_identity = 1
                                                """)
            .With("table", qualifiedName)
            .ExecuteScalarAsync(ct);
        Convert.ToInt32(identity).ShouldBe(ordered || tolerateOptedInSchema ? 1 : 0);

        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await conn.CreateCommand("""
                                                           SELECT i.name, STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                                                           FROM sys.indexes i
                                                           JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                                           JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                                           WHERE i.object_id = OBJECT_ID(@table) AND i.is_primary_key = 0 AND ic.key_ordinal > 0
                                                           GROUP BY i.name
                                                           """)
                         .With("table", qualifiedName)
                         .ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) actual.Add(reader.GetString(0), reader.GetString(1));
        }

        actual.Keys.Count(x => x.EndsWith("_queue_order", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(ordered || tolerateOptedInSchema ? 1 : 0);

        foreach (var index in table.Indexes)
        {
            actual.TryGetValue(index.Name, out var columns).ShouldBeTrue($"Missing index {index.Name}");
            columns.ShouldBe(string.Join(",", index.Columns!), StringCompareShould.IgnoreCase);
        }
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

    private sealed record StartedQueue(IHost Host, SqlServerQueue Queue);
}
