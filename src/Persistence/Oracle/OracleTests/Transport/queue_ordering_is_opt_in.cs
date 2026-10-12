using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Xunit;

namespace OracleTests.Transport;

/// <summary>
/// GH-4862. Oracle queues hand back each batch in timestamp order, and EnableDeterministicQueueOrdering()
/// adds an identity tie-breaker for rows that share a timestamp.
/// </summary>
public class queue_ordering_is_opt_in : OracleContext
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task default_configuration_keeps_the_legacy_queue_schema()
    {
        var queueName = newQueueName();
        IHost? host = null;
        try
        {
            var started = await startAsync(queueName, deterministicOrdering: false);
            host = started.Host;

            await assertQueueSchemaAsync(started.Queue.QueueTable, ordered: false);
            await assertQueueSchemaAsync(started.Queue.ScheduledTable, ordered: false);
            (await started.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, queueName);
        }
    }

    [Fact]
    public async Task ordering_can_be_enabled_through_the_message_transport_configuration()
    {
        var queueName = newQueueName();
        IHost? host = null;
        try
        {
            var started = await startAsync(queueName, deterministicOrdering: true);
            host = started.Host;

            started.Queue.Parent.EnableDeterministicQueueOrdering.ShouldBeTrue();
            await assertQueueSchemaAsync(started.Queue.QueueTable, ordered: true);
            await assertQueueSchemaAsync(started.Queue.ScheduledTable, ordered: true);
            (await started.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(host, queueName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task timestamp_order_works_without_the_optional_column(bool buffered)
    {
        var queueName = newQueueName();
        IHost? host = null;
        var envelopes = new List<Envelope>();
        try
        {
            var started = await startAsync(queueName, deterministicOrdering: false, buffered: buffered);
            host = started.Host;
            var queue = started.Queue;

            await sendAsync(queue, envelopes, 10);

            // Reverse the timestamp order relative to insert order so the test proves the pop
            // hands rows back by timestamp rather than in the order they were written
            await using (var conn = await queue.DataSource.OpenConnectionAsync(ct))
            {
                for (var i = 0; i < envelopes.Count; i++)
                {
                    await using var update = conn.CreateCommand(
                        $"UPDATE {queue.QueueTable.Identifier.QualifiedName} SET timestamp = :timestamp WHERE id = :id");
                    update.Parameters.Add(new OracleParameter("timestamp", OracleDbType.TimeStampTZ)
                    {
                        Value = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMicroseconds(envelopes.Count - i)
                    });
                    update.With("id", envelopes[i].Id);
                    (await update.ExecuteNonQueryAsync(ct)).ShouldBe(1);
                }
            }

            var received = await popAsync(host, queue, envelopes.Count, buffered);

            received.Select(x => x.Id).ToArray()
                .ShouldBe(envelopes.Select(x => x.Id).Reverse().ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, queueName, envelopes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task tied_timestamps_pop_in_send_order_when_enabled(bool buffered)
    {
        var queueName = newQueueName();
        IHost? host = null;
        var envelopes = new List<Envelope>();
        try
        {
            var started = await startAsync(queueName, deterministicOrdering: true, buffered: buffered);
            host = started.Host;
            var queue = started.Queue;

            await sendAsync(queue, envelopes, 10);
            await tieTimestampsAsync(queue.QueueTable);

            var received = await popAsync(host, queue, envelopes.Count, buffered);

            received.Select(x => x.Id).ToArray().ShouldBe(envelopes.Select(x => x.Id).ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, queueName, envelopes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task tied_timestamps_pop_by_the_tie_breaker_rather_than_storage_order(bool buffered)
    {
        var queueName = newQueueName();
        IHost? host = null;
        var envelopes = new List<Envelope>();
        try
        {
            var started = await startAsync(queueName, deterministicOrdering: true, buffered: buffered);
            host = started.Host;
            var queue = started.Queue;

            await sendAsync(queue, envelopes, 10);
            await tieTimestampsAsync(queue.QueueTable);

            // Rows written one after another are usually stored, scanned and sorted in that same
            // order, so a pop that ignored the tie-breaker would still look right in the test above.
            // Run the tie-breaker against the storage order to prove the pop actually sorts by it.
            await reverseQueueOrderAsync(queue.QueueTable, envelopes);

            var received = await popAsync(host, queue, envelopes.Count, buffered);

            received.Select(x => x.Id).ToArray()
                .ShouldBe(envelopes.Select(x => x.Id).Reverse().ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, queueName, envelopes);
        }
    }

    [Fact]
    public async Task pop_takes_no_more_than_the_requested_batch_in_order()
    {
        var queueName = newQueueName();
        IHost? host = null;
        var envelopes = new List<Envelope>();
        try
        {
            var started = await startAsync(queueName, deterministicOrdering: true);
            host = started.Host;
            var queue = started.Queue;

            await sendAsync(queue, envelopes, 7);
            await tieTimestampsAsync(queue.QueueTable);

            var first = await popAsync(host, queue, 3, buffered: true);
            var second = await popAsync(host, queue, 3, buffered: false);
            var third = await popAsync(host, queue, 3, buffered: true);

            first.Select(x => x.Id).ShouldBe(envelopes.Take(3).Select(x => x.Id));
            second.Select(x => x.Id).ShouldBe(envelopes.Skip(3).Take(3).Select(x => x.Id));
            third.Select(x => x.Id).ShouldBe(envelopes.Skip(6).Select(x => x.Id));
            (await queue.CountAsync()).ShouldBe(0);
        }
        finally
        {
            await stopAndDropAsync(host, queueName, envelopes);
        }
    }

    [Fact]
    public async Task scheduled_messages_are_promoted_in_execution_time_order_without_the_optional_column()
    {
        var queueName = newQueueName();
        IHost? host = null;
        var envelopes = new List<Envelope>();
        try
        {
            var started = await startAsync(queueName, deterministicOrdering: false);
            host = started.Host;
            var queue = started.Queue;

            await scheduleAsync(queue, envelopes, 10);

            // Due execution times in the reverse of the order the rows were written
            var due = DateTimeOffset.UtcNow.AddHours(-1);
            await using (var conn = await queue.DataSource.OpenConnectionAsync(ct))
            {
                for (var i = 0; i < envelopes.Count; i++)
                {
                    await using var update = conn.CreateCommand(
                        $"UPDATE {queue.ScheduledTable.Identifier.QualifiedName} SET {DatabaseConstants.ExecutionTime} = :time WHERE id = :id");
                    update.Parameters.Add(new OracleParameter("time", OracleDbType.TimeStampTZ)
                    {
                        Value = due.AddSeconds(-i)
                    });
                    update.With("id", envelopes[i].Id);
                    (await update.ExecuteNonQueryAsync(ct)).ShouldBe(1);
                }
            }

            var listener = buildListener(host, queue);
            await using (listener)
            {
                (await listener.MoveScheduledToReadyQueueAsync(ct)).ShouldBe(envelopes.Count);
            }

            (await queue.ScheduledCountAsync()).ShouldBe(0);
            var received = await popAsync(host, queue, envelopes.Count, buffered: false);
            received.Select(x => x.Id).ToArray()
                .ShouldBe(envelopes.Select(x => x.Id).Reverse().ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, queueName, envelopes);
        }
    }

    [Fact]
    public async Task scheduled_ties_are_promoted_in_order()
    {
        var queueName = newQueueName();
        IHost? host = null;
        var envelopes = new List<Envelope>();
        try
        {
            var started = await startAsync(queueName, deterministicOrdering: true);
            host = started.Host;
            var queue = started.Queue;
            const int total = 23;

            await scheduleAsync(queue, envelopes, total);

            // Every scheduled row is due at the same instant
            await using (var conn = await queue.DataSource.OpenConnectionAsync(ct))
            {
                await using var update = conn.CreateCommand(
                    $"UPDATE {queue.ScheduledTable.Identifier.QualifiedName} SET timestamp = :timestamp, {DatabaseConstants.ExecutionTime} = :time");
                update.Parameters.Add(new OracleParameter("timestamp", OracleDbType.TimeStampTZ) { Value = DateTimeOffset.UtcNow });
                update.Parameters.Add(new OracleParameter("time", OracleDbType.TimeStampTZ) { Value = DateTimeOffset.UtcNow.AddMinutes(-1) });
                (await update.ExecuteNonQueryAsync(ct)).ShouldBe(total);
            }

            // Same reasoning as tied_timestamps_pop_by_the_tie_breaker_rather_than_storage_order:
            // promotion has to follow the scheduled table's tie-breaker, not the storage order
            await reverseQueueOrderAsync(queue.ScheduledTable, envelopes);

            var listener = buildListener(host, queue);
            await using (listener)
            {
                // Oracle promotes every due row in one pass
                (await listener.MoveScheduledToReadyQueueAsync(ct)).ShouldBe(total);
            }

            (await queue.ScheduledCountAsync()).ShouldBe(0);
            (await queue.CountAsync()).ShouldBe(total);

            // Promotion inserts row by row inside one transaction, so the ready rows can share a
            // timestamp too. Tie them outright to prove the ready table's tie-breaker kept the order.
            await tieTimestampsAsync(queue.QueueTable);

            var received = await popAsync(host, queue, total, buffered: false);
            received.Select(x => x.Id).ToArray().ShouldBe(envelopes.Select(x => x.Id).Reverse().ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, queueName, envelopes);
        }
    }

    [Fact]
    public async Task disabling_after_upgrade_preserves_the_opted_in_schema_and_still_dequeues()
    {
        var queueName = newQueueName();
        IHost? optedInHost = null;
        IHost? disabledHost = null;
        var envelopes = new List<Envelope>();
        try
        {
            var optedIn = await startAsync(queueName, deterministicOrdering: true);
            optedInHost = optedIn.Host;
            await optedInHost.StopAsync(ct);
            optedInHost.Dispose();
            optedInHost = null;

            var disabled = await startAsync(queueName, deterministicOrdering: false);
            disabledHost = disabled.Host;
            await disabled.Queue.SetupAsync(NullLogger.Instance);

            await assertQueueSchemaAsync(disabled.Queue.QueueTable, ordered: false, tolerateOptedInSchema: true);
            await assertQueueSchemaAsync(disabled.Queue.ScheduledTable, ordered: false, tolerateOptedInSchema: true);
            (await disabled.Queue.CheckAsync()).ShouldBeTrue();

            await sendAsync(disabled.Queue, envelopes, 2);
            await scheduleAsync(disabled.Queue, envelopes, 1);

            var received = await popAsync(disabledHost, disabled.Queue, 5, buffered: false);
            received.Select(x => x.Id).ShouldBe(envelopes.Take(2).Select(x => x.Id));
            (await disabled.Queue.ScheduledCountAsync()).ShouldBe(1);
        }
        finally
        {
            await stopAndDropAsync(disabledHost, queueName, envelopes);
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
        var queueName = newQueueName();
        IHost? provisionHost = null;
        IHost? noCreateHost = null;
        try
        {
            var provisioned = await startAsync(queueName, deterministicOrdering);
            provisionHost = provisioned.Host;
            await provisionHost.StopAsync(ct);
            provisionHost.Dispose();
            provisionHost = null;

            var noCreate = await startAsync(queueName, deterministicOrdering, autoCreate: AutoCreate.None);
            noCreateHost = noCreate.Host;
            await assertQueueSchemaAsync(noCreate.Queue.QueueTable, ordered: deterministicOrdering);
            await assertQueueSchemaAsync(noCreate.Queue.ScheduledTable, ordered: deterministicOrdering);
            (await noCreate.Queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await stopAndDropAsync(noCreateHost, queueName);
            if (provisionHost != null)
            {
                await provisionHost.StopAsync(ct);
                provisionHost.Dispose();
            }
        }
    }

    // Short enough that every derived table and index name stays well inside Oracle's limits
    private static string newQueueName() => $"QO_{Guid.NewGuid().ToString("N")[..10]}".ToUpperInvariant();

    private static async Task<StartedQueue> startAsync(string queueName, bool deterministicOrdering,
        bool buffered = false, AutoCreate autoCreate = AutoCreate.CreateOrUpdate)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.AutoBuildMessageStorageOnStartup = autoCreate;
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();

                var persistence = opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");

                // Register the queue first to verify the transport option is read when the lazily
                // constructed queue tables are materialized during startup
                // The running listener must never take the rows the test is going to pop itself
                var listener = opts.ListenToOracleQueue(queueName)
                    .PollingInterval(1.Hours())
                    .MaximumMessagesToReceive(0);
                if (buffered)
                {
                    listener.BufferedInMemory();
                }

                persistence.EnableMessageTransport(x =>
                {
                    if (deterministicOrdering)
                    {
                        x.EnableDeterministicQueueOrdering();
                    }
                });
            }).StartAsync(ct);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<OracleTransport>();
        var queue = transport.Queues[queueName];
        queue.MaximumMessagesToReceive.ShouldBe(0);
        return new StartedQueue(host, queue);
    }

    private static OracleQueueListener buildListener(IHost host, OracleQueue queue)
    {
        var runtime = host.GetRuntime();
        var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
        return new OracleQueueListener(queue, runtime, receiver, queue.DataSource, null);
    }

    private static async Task<IReadOnlyList<Envelope>> popAsync(IHost host, OracleQueue queue, int count, bool buffered)
    {
        var runtime = host.GetRuntime();
        await using var listener = buildListener(host, queue);
        return buffered
            ? await listener.TryPopAsync(count, NullLogger.Instance, ct)
            : await listener.TryPopDurablyAsync(count, runtime.DurabilitySettings, NullLogger.Instance, ct);
    }

    private static async Task sendAsync(OracleQueue queue, List<Envelope> envelopes, int count)
    {
        var sender = new OracleQueueSender(queue);
        for (var i = 0; i < count; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelopes.Add(envelope);
            await sender.SendAsync(envelope, ct);
        }
    }

    private static async Task scheduleAsync(OracleQueue queue, List<Envelope> envelopes, int count)
    {
        var sender = new OracleQueueSender(queue);
        for (var i = 0; i < count; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
            envelopes.Add(envelope);
            await sender.SendAsync(envelope, ct);
        }
    }

    private static async Task tieTimestampsAsync(Table table)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(ct);
        await using var update = conn.CreateCommand($"UPDATE {table.Identifier.QualifiedName} SET timestamp = :timestamp");
        update.Parameters.Add(new OracleParameter("timestamp", OracleDbType.TimeStampTZ)
        {
            Value = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        });
        await update.ExecuteNonQueryAsync(ct);
    }

    private static async Task reverseQueueOrderAsync(Table table, IReadOnlyList<Envelope> envelopes)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(ct);
        for (var i = 0; i < envelopes.Count; i++)
        {
            await using var update = conn.CreateCommand(
                $"UPDATE {table.Identifier.QualifiedName} SET {QueueTable.QueueOrderColumn} = :queueOrder WHERE id = :id");
            update.With("queueOrder", 1_000_000L - i);
            update.With("id", envelopes[i].Id);
            (await update.ExecuteNonQueryAsync(ct)).ShouldBe(1);
        }
    }

    internal static async Task assertQueueSchemaAsync(Table table, bool ordered, bool tolerateOptedInSchema = false)
    {
        var expectOrdered = ordered || tolerateOptedInSchema;
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(ct);

        await using (var column = conn.CreateCommand(
                         "SELECT identity_column FROM all_tab_columns WHERE owner = :owner AND table_name = :tableName AND column_name = 'QUEUE_ORDER'"))
        {
            column.With("owner", table.Identifier.Schema.ToUpperInvariant());
            column.With("tableName", table.Identifier.Name.ToUpperInvariant());
            var identity = (string?)await column.ExecuteScalarAsync(ct);
            if (expectOrdered)
            {
                identity.ShouldBe("YES");
            }
            else
            {
                identity.ShouldBeNull();
            }
        }

        var actual = await readIndexesAsync(conn, table);

        var expectedIndexes = table.Indexes
            .Where(x => expectOrdered || !x.Columns!.Contains(QueueTable.QueueOrderColumn));
        foreach (var index in expectedIndexes)
        {
            actual.TryGetValue(index.Name.ToUpperInvariant(), out var columns).ShouldBeTrue($"Missing index {index.Name}");

            // Oracle indexes a TIMESTAMP WITH TIME ZONE column through SYS_EXTRACT_UTC(), which
            // shows up as a hidden SYS_NC column. Compare the plain columns by name and the rest by position.
            var expected = index.Columns!.Select(x => x.ToUpperInvariant()).ToArray();
            columns!.Length.ShouldBe(expected.Length, $"Index {index.Name} has {string.Join(",", columns)}");
            for (var i = 0; i < expected.Length; i++)
            {
                if (!columns[i].StartsWith("SYS_NC", StringComparison.Ordinal))
                {
                    columns[i].ShouldBe(expected[i]);
                }
            }
        }

        // The ready table has no legacy index; the scheduled table has the execution_time index
        var legacyIndexes = table is ScheduledMessageTable ? 1 : 0;
        actual.Count.ShouldBe(legacyIndexes + (expectOrdered ? 1 : 0));
    }

    private static async Task<Dictionary<string, string[]>> readIndexesAsync(OracleConnection conn, Table table)
    {
        await using var cmd = conn.CreateCommand("""
            SELECT index_name, LISTAGG(column_name, ',') WITHIN GROUP (ORDER BY column_position)
            FROM all_ind_columns
            WHERE table_owner = :owner AND table_name = :tableName AND index_name LIKE 'IDX%'
            GROUP BY index_name
            """);
        cmd.With("owner", table.Identifier.Schema.ToUpperInvariant());
        cmd.With("tableName", table.Identifier.Name.ToUpperInvariant());

        var actual = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            actual.Add(reader.GetString(0), reader.GetString(1).Split(','));
        }

        return actual;
    }

    internal static async Task stopAndDropAsync(IHost? host, string queueName, IReadOnlyList<Envelope>? envelopes = null)
    {
        if (host != null)
        {
            await host.StopAsync(ct);
            host.Dispose();
        }

        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(ct);

        // Wait out a poll that was still in flight when the host stopped instead of failing on ORA-00054
        await using (var wait = conn.CreateCommand("ALTER SESSION SET DDL_LOCK_TIMEOUT = 30"))
        {
            await wait.ExecuteNonQueryAsync(ct);
        }

        foreach (var table in new[] { $"WOLVERINE_QUEUE_{queueName}", $"WOLVERINE_QUEUE_{queueName}_SCHEDULED" })
        {
            try
            {
                await using var drop = conn.CreateCommand($"DROP TABLE WOLVERINE_QUEUES.{table} CASCADE CONSTRAINTS PURGE");
                await drop.ExecuteNonQueryAsync(ct);
            }
            catch (OracleException e) when (e.Number == 942)
            {
                // Never created
            }
        }

        // Durable pops leave the messages in the inbox. Remove them so no later test recovers them.
        foreach (var envelope in envelopes ?? [])
        {
            await using var delete = conn.CreateCommand(
                $"DELETE FROM WOLVERINE.{DatabaseConstants.IncomingTable} WHERE id = :id");
            delete.With("id", envelope.Id);
            await delete.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed record StartedQueue(IHost Host, OracleQueue Queue);
}
