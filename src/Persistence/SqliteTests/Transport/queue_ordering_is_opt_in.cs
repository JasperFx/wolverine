using JasperFx;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Weasel.Sqlite.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace SqliteTests.Transport;

/// <summary>
/// GH-4862. SQLite's queue timestamps come from datetime('now'), which has one-second precision, so
/// messages sent in the same second tie. EnableDeterministicQueueOrdering() breaks those ties with the
/// table's implicit rowid and adds an index to each queue table; without it the schema is unchanged.
/// Scheduled promotion follows execution time either way.
/// </summary>
public class queue_ordering_is_opt_in : SqliteContext
{
    private const string QueueName = "ordered";

    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task default_configuration_keeps_the_legacy_queue_schema()
    {
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));
        var started = await startAsync(database, deterministicOrdering: false);
        try
        {
            (await indexesOnAsync(database, started.Queue.QueueTable)).ShouldBeEmpty();
            (await indexesOnAsync(database, started.Queue.ScheduledTable)).ShouldBeEmpty();
            (await started.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAsync(started.Host);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ordering_can_be_enabled_through_both_public_configuration_routes(
        bool usePersistenceConfiguration)
    {
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));
        var started = await startAsync(database, deterministicOrdering: true,
            usePersistenceConfiguration: usePersistenceConfiguration);
        try
        {
            await assertOrderedSchemaAsync(database, started.Queue);
            (await started.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAsync(started.Host);
        }
    }

    [Fact]
    public async Task the_ordering_indexes_serve_the_tie_broken_queries_without_a_sort()
    {
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));
        var started = await startAsync(database, deterministicOrdering: true);
        try
        {
            var queue = started.Queue;

            // SQLite keeps the rowid as the last key of every index entry, so an index on timestamp alone
            // is already in (timestamp, rowid) order. That is why the option adds no column.
            var dequeue = await queryPlanAsync(database,
                $"SELECT id, body FROM {queue.QueueTable.Identifier} ORDER BY timestamp, rowid LIMIT 20");
            dequeue.ShouldContain($"USING INDEX idx_{queue.QueueTable.Identifier.Name}_timestamp");
            dequeue.ShouldNotContain("TEMP B-TREE");

            var promotion = await queryPlanAsync(database,
                $"SELECT id, body, message_type FROM {queue.ScheduledTable.Identifier} WHERE julianday(execution_time) <= julianday('now') AND id NOT IN (SELECT id FROM {queue.QueueTable.Identifier}) ORDER BY execution_time, rowid");
            promotion.ShouldContain($"USING INDEX idx_{queue.ScheduledTable.Identifier.Name}_execution_time");
            promotion.ShouldNotContain("TEMP B-TREE");
        }
        finally
        {
            await stopAsync(started.Host);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task pops_return_rows_in_timestamp_order_without_the_option(bool buffered)
    {
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));
        var started = await startAsync(database, deterministicOrdering: false);
        try
        {
            var queue = started.Queue;
            var envelopes = await sendAsync(queue, 10);

            // Timestamps run opposite to the order the rows were written, so only an ORDER BY timestamp
            // on the pop puts them back in this order
            await using (var conn = await openAsync(database))
            {
                for (var i = 0; i < envelopes.Count; i++)
                {
                    await using var update = conn.CreateCommand();
                    update.CommandText = $"UPDATE {queue.QueueTable.Identifier} SET timestamp = @timestamp WHERE id = @id";
                    update.Parameters.AddWithValue("timestamp", $"2026-01-01 00:00:{envelopes.Count - i:00}");
                    update.Parameters.AddWithValue("id", envelopes[i].Id.ToString("D"));
                    (await update.ExecuteNonQueryAsync(ct)).ShouldBe(1);
                }
            }

            var received = await popAsync(started, buffered, envelopes.Count);

            received.Select(x => x.Id).ToArray()
                .ShouldBe(envelopes.Select(x => x.Id).Reverse().ToArray());
        }
        finally
        {
            await stopAsync(started.Host);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task tied_timestamps_pop_in_send_order_with_the_option(bool buffered)
    {
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));
        var started = await startAsync(database, deterministicOrdering: true);
        try
        {
            var queue = started.Queue;
            var envelopes = await sendAsync(queue, 25);

            // The common case on SQLite anyway, made certain: every row has the same timestamp
            await executeAsync(database, $"UPDATE {queue.QueueTable.Identifier} SET timestamp = '2026-01-01 00:00:00'");

            // Popped in two batches, so the batch boundary is chosen by the tie-breaker as well
            var first = await popAsync(started, buffered, 10);
            var second = await popAsync(started, buffered, 100);

            first.Concat(second).Select(x => x.Id).ToArray()
                .ShouldBe(envelopes.Select(x => x.Id).ToArray());
        }
        finally
        {
            await stopAsync(started.Host);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task scheduled_messages_are_promoted_in_execution_time_order(bool deterministicOrdering)
    {
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));
        var started = await startAsync(database, deterministicOrdering);
        try
        {
            var queue = started.Queue;
            var sender = new SqliteQueueSender(queue);
            var envelopes = new List<Envelope>();

            // Scheduled in the opposite order to the one they are due in, and already due
            var due = DateTimeOffset.UtcNow.AddMinutes(-1);
            for (var i = 0; i < 10; i++)
            {
                var envelope = ObjectMother.Envelope();
                envelope.ScheduledTime = due.AddSeconds(-i);
                envelopes.Add(envelope);
                await sender.ScheduleMessageAsync(envelope, ct);
            }

            await using var listener = buildListener(started, 100);
            (await listener.MoveScheduledToReadyQueueAsync(ct)).ShouldBe(10);

            var expected = envelopes.Select(x => x.Id).Reverse().ToArray();

            // The promoted rows' insertion order is the order the tie-breaker sees
            (await readyIdsInRowidOrderAsync(database, queue)).ShouldBe(expected);

            if (deterministicOrdering)
            {
                // Promotion stamps every row with the same timestamp, so the pop order is the rowid order
                var received = await listener.TryPopDurablyAsync(ct);
                received.Select(x => x.Id).ToArray().ShouldBe(expected);
            }
        }
        finally
        {
            await stopAsync(started.Host);
        }
    }

    [Fact]
    public async Task scheduled_ties_are_promoted_and_popped_in_scheduling_order_with_the_option()
    {
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));
        var started = await startAsync(database, deterministicOrdering: true);
        try
        {
            var queue = started.Queue;
            var sender = new SqliteQueueSender(queue);
            var envelopes = new List<Envelope>();

            for (var i = 0; i < 23; i++)
            {
                var envelope = ObjectMother.Envelope();
                envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
                envelopes.Add(envelope);
                await sender.ScheduleMessageAsync(envelope, ct);
            }

            await executeAsync(database,
                $"UPDATE {queue.ScheduledTable.Identifier} SET execution_time = '2026-01-01 00:00:00.0000000', timestamp = '2026-01-01 00:00:00'");

            await using var listener = buildListener(started, 100);
            (await listener.MoveScheduledToReadyQueueAsync(ct)).ShouldBe(23);
            (await queue.ScheduledCountAsync()).ShouldBe(0);

            var received = await listener.TryPopDurablyAsync(ct);
            received.Select(x => x.Id).ToArray().ShouldBe(envelopes.Select(x => x.Id).ToArray());
        }
        finally
        {
            await stopAsync(started.Host);
        }
    }

    [Fact]
    public async Task a_message_that_comes_due_during_promotion_is_not_deleted_unpromoted()
    {
        // The promotion INSERT and DELETE used to evaluate julianday('now') separately, so a message that came due
        // between the two statements was deleted from the scheduled table without reaching the ready queue. A
        // cutoff 30 seconds in the past stands in for the INSERT's 'now'; the message due 10 seconds ago came
        // due "between" the statements
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));
        var started = await startAsync(database, deterministicOrdering: false);
        try
        {
            var queue = started.Queue;
            var sender = new SqliteQueueSender(queue);

            var dueBeforeCutoff = ObjectMother.Envelope();
            dueBeforeCutoff.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1);
            await sender.ScheduleMessageAsync(dueBeforeCutoff, ct);

            var dueAfterCutoff = ObjectMother.Envelope();
            dueAfterCutoff.ScheduledTime = DateTimeOffset.UtcNow.AddSeconds(-10);
            await sender.ScheduleMessageAsync(dueAfterCutoff, ct);

            await using var conn = await openAsync(database);
            var cutoffCommand = conn.CreateCommand();
            cutoffCommand.CommandText = "SELECT julianday('now', '-30 seconds')";
            var cutoff = Convert.ToDouble(await cutoffCommand.ExecuteScalarAsync(ct));

            await using var listener = buildListener(started, 100);
            (await listener.MoveScheduledToReadyQueueAsync(cutoff, ct)).ShouldBe(1);

            (await queue.CountAsync()).ShouldBe(1);
            (await queue.ScheduledCountAsync()).ShouldBe(1);

            // ...and the next promotion picks it up
            (await listener.MoveScheduledToReadyQueueAsync(ct)).ShouldBe(1);
            (await queue.CountAsync()).ShouldBe(2);
            (await queue.ScheduledCountAsync()).ShouldBe(0);
        }
        finally
        {
            await stopAsync(started.Host);
        }
    }

    [Fact]
    public async Task disabling_after_upgrade_preserves_the_opted_in_schema_and_still_dequeues()
    {
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));

        var optedIn = await startAsync(database, deterministicOrdering: true);
        await stopAsync(optedIn.Host);

        var disabled = await startAsync(database, deterministicOrdering: false);
        try
        {
            await disabled.Queue.SetupAsync(NullLogger.Instance);

            // The add-only tables keep what the opted-in configuration added
            await assertOrderedSchemaAsync(database, disabled.Queue);
            (await disabled.Queue.CheckAsync()).ShouldBeTrue();

            var envelopes = await sendAsync(disabled.Queue, 1);
            var received = await popAsync(disabled, buffered: false, 1);
            received.Select(x => x.Id).ShouldBe(envelopes.Select(x => x.Id));
        }
        finally
        {
            await stopAsync(disabled.Host);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task auto_create_none_uses_a_preprovisioned_schema_matching_the_ordering_setting(
        bool deterministicOrdering)
    {
        using var database = Servers.CreateDatabase(nameof(queue_ordering_is_opt_in));

        var provisioned = await startAsync(database, deterministicOrdering);
        await stopAsync(provisioned.Host);

        var noCreate = await startAsync(database, deterministicOrdering, autoCreate: AutoCreate.None);
        try
        {
            if (deterministicOrdering)
            {
                await assertOrderedSchemaAsync(database, noCreate.Queue);
            }
            else
            {
                (await indexesOnAsync(database, noCreate.Queue.QueueTable)).ShouldBeEmpty();
                (await indexesOnAsync(database, noCreate.Queue.ScheduledTable)).ShouldBeEmpty();
            }

            (await noCreate.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAsync(noCreate.Host);
        }
    }

    private static async Task<StartedQueue> startAsync(SqliteTestDatabase database, bool deterministicOrdering,
        bool usePersistenceConfiguration = false, AutoCreate autoCreate = AutoCreate.CreateOrUpdate)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
                opts.AutoBuildMessageStorageOnStartup = autoCreate;
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();

                // Publish-only: nothing in the host pops the queue, so the listeners the tests build are
                // the only readers. The queue is registered BEFORE the option is set, which proves the
                // lazily built queue tables and the listener read the final configuration.
                if (usePersistenceConfiguration)
                {
                    var persistence = opts.PersistMessagesWithSqlite(database.ConnectionString);
                    opts.PublishAllMessages().ToSqliteQueue(QueueName);
                    persistence.EnableMessageTransport(expression =>
                    {
                        if (deterministicOrdering)
                        {
                            expression.EnableDeterministicQueueOrdering();
                        }
                    });
                }
                else
                {
                    var expression = opts.UseSqlitePersistenceAndTransport(database.ConnectionString);
                    opts.PublishAllMessages().ToSqliteQueue(QueueName);
                    if (deterministicOrdering)
                    {
                        expression.EnableDeterministicQueueOrdering();
                    }
                }
            }).StartAsync(ct);

        var queue = host.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>().Queues[QueueName];
        return new StartedQueue(host, queue);
    }

    private static async Task<List<Envelope>> sendAsync(SqliteQueue queue, int count)
    {
        var envelopes = new List<Envelope>();
        for (var i = 0; i < count; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelopes.Add(envelope);
            await queue.SendAsync(envelope);
        }

        (await queue.CountAsync()).ShouldBe(count);
        return envelopes;
    }

    private static SqliteQueueListener buildListener(StartedQueue started, int batchSize)
    {
        // The batch size is baked into the listener's SQL when it is built
        started.Queue.MaximumMessagesToReceive = batchSize;
        return new SqliteQueueListener(started.Queue, started.Host.GetRuntime(), Substitute.For<IReceiver>(),
            started.Queue.DataSource, null);
    }

    private static async Task<IReadOnlyList<Envelope>> popAsync(StartedQueue started, bool buffered, int batchSize)
    {
        await using var listener = buildListener(started, batchSize);
        return buffered
            ? await listener.TryPopAsync(ct)
            : await listener.TryPopDurablyAsync(ct);
    }

    private static async Task<Guid[]> readyIdsInRowidOrderAsync(SqliteTestDatabase database, SqliteQueue queue)
    {
        await using var conn = await openAsync(database);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM {queue.QueueTable.Identifier} ORDER BY rowid";
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids.ToArray();
    }

    private static async Task assertOrderedSchemaAsync(SqliteTestDatabase database, SqliteQueue queue)
    {
        var ready = await indexesOnAsync(database, queue.QueueTable);
        ready.Keys.ToArray().ShouldBe(new[] { $"idx_{queue.QueueTable.Identifier.Name}_timestamp" });
        ready.Values.Single().ShouldContain("(timestamp)");

        var scheduled = await indexesOnAsync(database, queue.ScheduledTable);
        scheduled.Keys.ToArray().ShouldBe(new[] { $"idx_{queue.ScheduledTable.Identifier.Name}_execution_time" });
        scheduled.Values.Single().ShouldContain("(execution_time)");
    }

    internal static async Task<Dictionary<string, string>> indexesOnAsync(SqliteTestDatabase database, Table table)
    {
        await using var conn = await openAsync(database);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = @table AND name LIKE 'idx_%'";
        cmd.Parameters.AddWithValue("table", table.Identifier.Name);

        var indexes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            indexes.Add(reader.GetString(0), reader.GetString(1).Replace("\"", string.Empty));
        }

        return indexes;
    }

    private static async Task<string> queryPlanAsync(SqliteTestDatabase database, string sql)
    {
        await using var conn = await openAsync(database);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;

        var plan = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            plan.Add(reader.GetString(3));
        }

        return string.Join(Environment.NewLine, plan);
    }

    private static async Task executeAsync(SqliteTestDatabase database, string sql)
    {
        await using var conn = await openAsync(database);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
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

    private sealed record StartedQueue(IHost Host, SqliteQueue Queue);
}
