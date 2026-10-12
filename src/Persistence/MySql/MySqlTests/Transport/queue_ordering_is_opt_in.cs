using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using NSubstitute;
using Shouldly;
using Weasel.MySql.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;

namespace MySqlTests.Transport;

/// <summary>
/// GH-4862. Dequeue hands rows over in timestamp order by default, and EnableDeterministicQueueOrdering()
/// adds a persistent AUTO_INCREMENT tie-breaker for rows whose timestamps are equal.
/// </summary>
[Collection("mysql")]
public class queue_ordering_is_opt_in
{
    private const string QueueName = "ordered";
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task default_configuration_keeps_the_legacy_queue_schema()
    {
        var schema = await newSchemaAsync();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: false);
            host = started.Host;

            await assertQueueSchemaAsync(started.QueueTable, ordered: false);
            await assertQueueSchemaAsync(started.ScheduledTable, ordered: false);
            (await started.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Theory]
    [InlineData(false, "a_queue_name_of_thirty_letters")]
    [InlineData(true, "a_queue_name_of_thirty_letters")]
    [InlineData(false, "a_queue_name_that_is_sixty_characters_long_for_mysql_limits")]
    [InlineData(true, "a_queue_name_that_is_sixty_characters_long_for_mysql_limits")]
    public async Task a_long_queue_name_keeps_every_identifier_within_the_mysql_limit(bool deterministicOrdering,
        string queueName)
    {
        // MySQL refuses identifiers over 64 characters. The scheduled table's execution_time index passed it for
        // queue names over 19 characters, the scheduled table itself over 38 and the ready table over 48, and the
        // host failed to start
        var schema = await newSchemaAsync();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering, queueName: queueName);
            host = started.Host;

            started.QueueTable.Identifier.Name.Length.ShouldBeLessThanOrEqualTo(64);
            started.ScheduledTable.Identifier.Name.Length.ShouldBeLessThanOrEqualTo(64);
            started.QueueTable.Indexes.ShouldAllBe(x => x.Name.Length <= 64);
            started.ScheduledTable.Indexes.ShouldAllBe(x => x.Name.Length <= 64);
            (await started.Queue.CheckAsync()).ShouldBeTrue();

            // The shortened tables and the listener's temporary tables work end to end
            var runtime = host.GetRuntime();
            var queue = started.Queue;
            var sent = await sendAsync(queue, 2, descendingIds: false);

            var scheduled = ObjectMother.Envelope();
            scheduled.ScheduledTime = DateTimeOffset.UtcNow.AddSeconds(-1);
            await new MySqlQueueSender(queue).ScheduleMessageAsync(scheduled, ct);

            var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
            await using var listener = new MySqlQueueListener(queue, runtime, receiver, queue.DataSource, null);
            (await listener.MoveScheduledToReadyQueueAsync(ct)).ShouldBe(1);

            var received = await listener.TryPopDurablyAsync(3, runtime.DurabilitySettings, NullLogger.Instance, ct);
            received.Select(x => x.Id).ShouldBe(sent.Select(x => x.Id).Append(scheduled.Id), ignoreOrder: true);
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ordering_can_be_enabled_through_both_public_configuration_routes(
        bool usePersistenceConfiguration)
    {
        var schema = await newSchemaAsync();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: true,
                usePersistenceConfiguration: usePersistenceConfiguration);
            host = started.Host;

            await assertQueueSchemaAsync(started.QueueTable, ordered: true);
            await assertQueueSchemaAsync(started.ScheduledTable, ordered: true);
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
        var schema = await newSchemaAsync();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: false, buffered: buffered);
            host = started.Host;
            var queue = started.Queue;

            // Ids ascend in send order, so a batch handed back in primary key order is in send order
            var envelopes = await sendAsync(queue, 10, descendingIds: false);

            // Reverse the timestamp order relative to send order, so the test proves the batch comes back
            // in timestamp order and not in send or primary key order. Whole seconds, because the
            // timestamp column is a plain DATETIME.
            await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
            {
                await conn.OpenAsync(ct);
                for (var i = 0; i < envelopes.Count; i++)
                {
                    await using var update = conn.CreateCommand();
                    update.CommandText =
                        $"UPDATE {started.QueueTable.Identifier.QualifiedName} SET timestamp = @timestamp WHERE id = @id";
                    update.Parameters.AddWithValue("@id", envelopes[i].Id);
                    update.Parameters.AddWithValue("@timestamp",
                        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(envelopes.Count - i));
                    await update.ExecuteNonQueryAsync(ct);
                }
            }

            var received = await popAsync(started, envelopes.Count, buffered);

            received.Select(x => x.Id).ToArray()
                .ShouldBe(envelopes.Select(x => x.Id).Reverse().ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task tied_timestamps_pop_in_send_order_with_the_option(bool buffered)
    {
        var schema = await newSchemaAsync();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: true, buffered: buffered);
            host = started.Host;
            var queue = started.Queue;

            // Ids DESCEND in send order, so neither primary key order nor an arbitrary tie order can pass
            var envelopes = await sendAsync(queue, 10, descendingIds: true);

            await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var update = conn.CreateCommand();
                update.CommandText = $"UPDATE {started.QueueTable.Identifier.QualifiedName} SET timestamp = @timestamp";
                update.Parameters.AddWithValue("@timestamp", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                await update.ExecuteNonQueryAsync(ct);
            }

            var received = await popAsync(started, envelopes.Count, buffered);

            received.Select(x => x.Id).ToArray().ShouldBe(envelopes.Select(x => x.Id).ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Fact]
    public async Task scheduled_ties_are_promoted_in_order()
    {
        var schema = await newSchemaAsync();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: true);
            host = started.Host;
            var runtime = host.GetRuntime();
            var queue = started.Queue;
            const int total = 23;
            var envelopes = new List<Envelope>();
            var sender = new MySqlQueueSender(queue);

            foreach (var id in descendingIds(total))
            {
                var envelope = ObjectMother.Envelope();
                envelope.Id = id;
                envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
                envelopes.Add(envelope);
                await sender.ScheduleMessageAsync(envelope, ct);
            }

            // Every scheduled row comes due at the same moment with the same timestamp
            await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var update = conn.CreateCommand();
                update.CommandText = $"""
                    UPDATE {started.ScheduledTable.Identifier.QualifiedName}
                    SET timestamp = @timestamp, execution_time = @execution_time
                    """;
                update.Parameters.AddWithValue("@timestamp", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                update.Parameters.AddWithValue("@execution_time", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                await update.ExecuteNonQueryAsync(ct);
            }

            var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
            await using var listener = new MySqlQueueListener(queue, runtime, receiver, queue.DataSource, null);

            // MySQL promotes everything that is due in one statement
            (await listener.MoveScheduledToReadyQueueAsync(ct)).ShouldBe(total);

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
        var schema = await newSchemaAsync();
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

            await assertQueueSchemaAsync(disabled.QueueTable, ordered: false, tolerateOptedInSchema: true);
            await assertQueueSchemaAsync(disabled.ScheduledTable, ordered: false, tolerateOptedInSchema: true);
            (await disabled.Queue.CheckAsync()).ShouldBeTrue();

            var envelopes = await sendAsync(disabled.Queue, 2, descendingIds: false);

            var received = await popAsync(disabled, envelopes.Count, buffered: false);
            received.Select(x => x.Id).ShouldBe(envelopes.Select(x => x.Id), ignoreOrder: true);
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
        var schema = await newSchemaAsync();
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
            await assertQueueSchemaAsync(noCreate.QueueTable, ordered: deterministicOrdering);
            await assertQueueSchemaAsync(noCreate.ScheduledTable, ordered: deterministicOrdering);
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

    private static async Task<string> newSchemaAsync()
    {
        var schema = $"queue_order_{Guid.NewGuid():N}"[..40];
        await executeAsync($"CREATE DATABASE IF NOT EXISTS {schema}");
        return schema;
    }

    private static async Task<StartedQueue> startAsync(string schema, bool deterministicOrdering,
        bool usePersistenceConfiguration = false, bool buffered = false,
        AutoCreate autoCreate = AutoCreate.CreateOrUpdate, string queueName = QueueName)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.AutoBuildMessageStorageOnStartup = autoCreate;
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();

                if (usePersistenceConfiguration)
                {
                    var persistence = opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, schema);

                    // Register the queue first to verify the transport option is read when the lazily
                    // constructed queue tables are materialized during startup.
                    var listener = opts.ListenToMySqlQueue(queueName);
                    if (buffered) listener.BufferedInMemory();
                    listener.PollingInterval(1.Hours());

                    persistence.EnableMessageTransport(expression =>
                    {
                        expression.TransportSchemaName(schema);
                        if (deterministicOrdering)
                        {
                            expression.EnableDeterministicQueueOrdering();
                        }
                    });
                }
                else
                {
                    var expression = opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString,
                        schema, transportSchema: schema);
                    var listener = opts.ListenToMySqlQueue(queueName);
                    if (buffered) listener.BufferedInMemory();
                    listener.PollingInterval(1.Hours());

                    // The queue endpoint exists before the option is set; table construction is lazy and
                    // must use the final transport configuration.
                    if (deterministicOrdering)
                    {
                        expression.EnableDeterministicQueueOrdering();
                    }
                }
            }).StartAsync(ct);

        var runtime = host.GetRuntime();
        var queue = runtime.Options.Transports.GetOrCreate<MySqlTransport>().Queues[queueName];

        // Keep the host's own listener from draining the queue underneath the assertions
        await runtime.Endpoints.StopListenerAsync(queue, ct);

        return new StartedQueue(host, queue, queue.QueueTableFor(queue.DataSource),
            queue.ScheduledTableFor(queue.DataSource));
    }

    private static IReadOnlyList<Guid> descendingIds(int count) =>
        Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).OrderByDescending(x => x.ToString()).ToArray();

    private static async Task<List<Envelope>> sendAsync(MySqlQueue queue, int count, bool descendingIds)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).OrderBy(x => x.ToString()).ToList();
        if (descendingIds) ids.Reverse();

        var envelopes = new List<Envelope>();
        foreach (var id in ids)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Id = id;
            envelopes.Add(envelope);
            await queue.SendAsync(envelope);
        }

        (await queue.CountAsync()).ShouldBe(count);
        return envelopes;
    }

    private static async Task<IReadOnlyList<Envelope>> popAsync(StartedQueue started, int count, bool buffered)
    {
        var runtime = started.Host.GetRuntime();
        var receiver = new DurableReceiver(started.Queue, runtime, Substitute.For<IHandlerPipeline>());
        await using var listener = new MySqlQueueListener(started.Queue, runtime, receiver,
            started.Queue.DataSource, null);

        return buffered
            ? await listener.TryPopAsync(count, NullLogger.Instance, ct)
            : await listener.TryPopDurablyAsync(count, runtime.DurabilitySettings, NullLogger.Instance, ct);
    }

    internal static async Task assertQueueSchemaAsync(Table table, bool ordered, bool tolerateOptedInSchema = false)
    {
        var schema = table.Identifier.Schema!;
        var tableName = table.Identifier.Name;
        var expectOrdered = ordered || tolerateOptedInSchema;

        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);

        await using (var column = conn.CreateCommand())
        {
            column.CommandText = """
                                 SELECT count(*) FROM information_schema.columns
                                 WHERE table_schema = @schema AND table_name = @table AND column_name = 'queue_order'
                                 """;
            column.Parameters.AddWithValue("@schema", schema);
            column.Parameters.AddWithValue("@table", tableName);
            Convert.ToInt64(await column.ExecuteScalarAsync(ct)).ShouldBe(expectOrdered ? 1L : 0L);
        }

        var actual = await readIndexesAsync(conn, schema, tableName);

        // The legacy indexes, plus the unique key and the composite index when the option has been applied
        var legacyCount = table.Indexes.Count(x => !x.Columns.Contains(QueueOrdering.QueueOrderColumn));
        actual.Count.ShouldBe(legacyCount + (expectOrdered ? 2 : 0));

        foreach (var index in table.Indexes)
        {
            actual.TryGetValue(index.Name, out var columns).ShouldBeTrue($"Missing index {index.Name}");
            columns!.ShouldBe(index.Columns);
        }
    }

    internal static async Task<Dictionary<string, string[]>> readIndexesAsync(MySqlConnection conn, string schema,
        string tableName)
    {
        await using var indexes = conn.CreateCommand();
        indexes.CommandText = """
                              SELECT index_name, column_name FROM information_schema.statistics
                              WHERE table_schema = @schema AND table_name = @table AND index_name <> 'PRIMARY'
                              ORDER BY index_name, seq_in_index
                              """;
        indexes.Parameters.AddWithValue("@schema", schema);
        indexes.Parameters.AddWithValue("@table", tableName);

        var actual = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await indexes.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!actual.TryGetValue(name, out var columns))
            {
                columns = new List<string>();
                actual.Add(name, columns);
            }

            columns.Add(reader.GetString(1));
        }

        return actual.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
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

    private static async Task executeAsync(string sql)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record StartedQueue(IHost Host, MySqlQueue Queue, Table QueueTable, Table ScheduledTable);
}
