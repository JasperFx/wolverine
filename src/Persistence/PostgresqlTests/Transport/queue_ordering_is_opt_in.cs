using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.RDBMS;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

public class queue_ordering_is_opt_in : PostgresqlContext
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task default_configuration_keeps_the_legacy_queue_schema()
    {
        var schema = newSchema();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: false);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ordering_can_be_enabled_through_both_public_configuration_routes(
        bool usePersistenceConfiguration)
    {
        var schema = newSchema();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: true,
                usePersistenceConfiguration: usePersistenceConfiguration);
            host = started.Host;

            await assertQueueSchemaAsync(started.Queue.QueueTable, ordered: true);
            await assertQueueSchemaAsync(started.Queue.ScheduledTable, ordered: true);
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
            var runtime = host.GetRuntime();
            var queue = started.Queue;
            queue.MaximumMessagesToReceive = 0;

            var envelopes = new List<Envelope>();
            for (var i = 0; i < 10; i++)
            {
                var envelope = ObjectMother.Envelope();
                envelopes.Add(envelope);
                await queue.SendAsync(envelope);
            }

            await waitUntilAsync(async () => await queue.CountAsync() == envelopes.Count,
                $"Expected {envelopes.Count} ready messages");

            // Reverse the timestamp order relative to insert order so the test proves the SQL reapplies
            // timestamp ordering to DELETE ... RETURNING without needing queue_order in this schema.
            await using (var conn = await queue.DataSource.OpenConnectionAsync(ct))
            {
                for (var i = 0; i < envelopes.Count; i++)
                {
                    await using var update = conn.CreateCommand($"UPDATE {queue.QueueTable.Identifier} SET timestamp = @timestamp WHERE id = @id");
                    update.Parameters.AddWithValue("id", envelopes[i].Id);
                    update.Parameters.AddWithValue("timestamp", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        .AddMicroseconds(envelopes.Count - i));
                    await update.ExecuteNonQueryAsync(ct);
                }
            }

            var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
            await using var listener = new PostgresqlQueueListener(queue, runtime, receiver, queue.DataSource, null);
            var received = buffered
                ? await listener.TryPopAsync(envelopes.Count, NullLogger.Instance, ct)
                : await listener.TryPopDurablyAsync(envelopes.Count, runtime.DurabilitySettings,
                    NullLogger.Instance, ct);

            received.Select(x => x.Id).ToArray()
                .ShouldBe(envelopes.Select(x => x.Id).Reverse().ToArray());
        }
        finally
        {
            await stopAndDropAsync(host, schema);
        }
    }

    [Fact]
    public async Task scheduled_ties_are_promoted_in_order_across_multiple_batches()
    {
        var schema = newSchema();
        IHost? host = null;
        try
        {
            var started = await startAsync(schema, deterministicOrdering: true, recoveryBatchSize: 7);
            host = started.Host;
            var runtime = host.GetRuntime();
            var queue = started.Queue;
            queue.MaximumMessagesToReceive = 0;
            const int total = 23;
            var envelopes = new List<Envelope>();
            var sender = new PostgresqlQueueSender(queue);

            for (var i = 0; i < total; i++)
            {
                var envelope = ObjectMother.Envelope();
                envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
                envelopes.Add(envelope);
                await sender.ScheduleMessageAsync(envelope, ct);
            }

            await using (var conn = await queue.DataSource.OpenConnectionAsync(ct))
            {
                await using var update = conn.CreateCommand($"""
                    UPDATE {queue.ScheduledTable.Identifier}
                    SET timestamp = @timestamp, execution_time = @execution_time
                    """);
                update.Parameters.AddWithValue("timestamp", DateTimeOffset.UtcNow);
                update.Parameters.AddWithValue("execution_time", DateTimeOffset.UtcNow.AddMinutes(-1));
                await update.ExecuteNonQueryAsync(ct);
            }

            var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
            await using var listener = new PostgresqlQueueListener(queue, runtime, receiver, queue.DataSource, null);
            var promoted = 0L;
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

            disabled.Queue.MaximumMessagesToReceive = 0;
            var envelope = ObjectMother.Envelope();
            await disabled.Queue.SendAsync(envelope);
            await waitUntilAsync(async () => await disabled.Queue.CountAsync() == 1,
                "Expected one ready message");

            var runtime = disabled.Host.GetRuntime();
            var receiver = new DurableReceiver(disabled.Queue, runtime, Substitute.For<IHandlerPipeline>());
            await using var listener = new PostgresqlQueueListener(disabled.Queue, runtime, receiver,
                disabled.Queue.DataSource, null);
            var received = await listener.TryPopDurablyAsync(1, runtime.DurabilitySettings,
                NullLogger.Instance, ct);
            received.Select(x => x.Id).ShouldBe(new[] { envelope.Id });
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

    private async Task<StartedQueue> startAsync(string schema, bool deterministicOrdering,
        bool usePersistenceConfiguration = false, bool buffered = false, int recoveryBatchSize = 100,
        AutoCreate autoCreate = AutoCreate.CreateOrUpdate)
    {
        const string queueName = "ordered";
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.AutoBuildMessageStorageOnStartup = autoCreate;
                opts.Durability.RecoveryBatchSize = recoveryBatchSize;
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();

                if (usePersistenceConfiguration)
                {
                    var persistence = opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
                    // Register the queue first to verify the transport option is read when the
                    // lazily constructed queue tables are materialized during startup.
                    var listener = opts.ListenToPostgresqlQueue(queueName);
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
                    var expression = opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString,
                        schema, transportSchema: schema);
                    opts.ListenToPostgresqlQueue(queueName).PollingInterval(1.Hours());
                    // The queue endpoint exists before the feature option is set; table construction is lazy
                    // and must use the final transport configuration.
                    if (deterministicOrdering)
                    {
                        expression.EnableDeterministicQueueOrdering();
                    }
                }

            }).StartAsync(ct);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>();
        var queue = transport.Queues[queueName];
        queue.MaximumMessagesToReceive = 0;
        return new StartedQueue(host, queue);
    }

    private static async Task assertQueueSchemaAsync(Table table, bool ordered, bool tolerateOptedInSchema = false)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);

        await using var column = conn.CreateCommand();
        column.CommandText = """
                             SELECT count(*) FROM information_schema.columns
                             WHERE table_schema = @schema AND table_name = @table AND column_name = 'queue_order';
                             """;
        column.Parameters.AddWithValue("schema", table.Identifier.Schema!);
        column.Parameters.AddWithValue("table", table.Identifier.Name);
        Convert.ToInt64(await column.ExecuteScalarAsync(ct)).ShouldBe(ordered || tolerateOptedInSchema ? 1L : 0L);

        await using var indexes = conn.CreateCommand();
        indexes.CommandText = """
                               SELECT indexname, indexdef FROM pg_indexes
                               WHERE schemaname = @schema AND tablename = @table AND indexname LIKE 'idx_%';
                               """;
        indexes.Parameters.AddWithValue("schema", table.Identifier.Schema!);
        indexes.Parameters.AddWithValue("table", table.Identifier.Name);
        await using var reader = await indexes.ExecuteReaderAsync(ct);
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct)) actual.Add(reader.GetString(0), reader.GetString(1));

        actual.Count.ShouldBe(ordered || tolerateOptedInSchema ? 2 : 1);
        foreach (var index in table.Indexes)
        {
            actual.TryGetValue(index.Name, out var definition).ShouldBeTrue();
            var expected = $"({string.Join(", ", index.Columns!)})";
            definition!.Replace("\"", string.Empty, StringComparison.Ordinal)
                .Contains(expected, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        }
    }

    private static async Task waitUntilAsync(Func<Task<bool>> condition, string message)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (await condition()) return;
            await Task.Delay(50, ct);
        }

        throw new TimeoutException(message);
    }

    private async Task stopAndDropAsync(IHost? host, string schema)
    {
        if (host != null)
        {
            await host.StopAsync(ct);
            host.Dispose();
        }

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await conn.DropSchemaAsync(schema, ct: ct);
    }

    private sealed record StartedQueue(IHost Host, PostgresqlQueue Queue);
}
