using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;

namespace SqliteTests.Transport;

/// <summary>
/// GH-5438, found by the durability chaos rig. Sending an outboxed envelope to a durable SQLite queue moves it from the
/// outbox to the queue table: an INSERT and then a DELETE, sent as one command. SQLite committed each statement on its
/// own, so a process killed between them left the envelope in the queue AND in the outbox.
/// Outbox recovery then sent it a second time. When the first copy had gone on to a scheduled retry, which deletes its
/// inbox row, nothing recognised the second copy as a duplicate, and the message was handled twice.
/// </summary>
public class outbox_to_queue_move_is_atomic : IAsyncLifetime
{
    private const string QueueName = "outboxmove";

    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(outbox_to_queue_move_is_atomic));

    private IHost _host = null!;
    private SqliteQueue _queue = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseSqlitePersistenceAndTransport(_database.ConnectionString).AutoProvision();

                // Declared without a listener, so nothing but the test touches the queue
                opts.PublishMessage<SqliteOutboxMoveMessage>().ToSqliteQueue(QueueName);
            }).StartAsync();

        _queue = _host.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>().Queues[QueueName];
        _queue.Mode = EndpointMode.Durable;
        await _queue.PurgeAsync(NullLogger.Instance);
        await _host.GetRuntime().Storage.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _database.Dispose();
    }

    private async Task executeAsync(string sql)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>What the outbox has done by the time the envelope is flushed to the queue</summary>
    private async Task<Envelope> persistedInTheOutboxAsync(DateTimeOffset? scheduledTime = null)
    {
        var runtime = _host.GetRuntime();
        var envelope = runtime.RoutingFor(typeof(SqliteOutboxMoveMessage))
            .RouteForSend(new SqliteOutboxMoveMessage(Guid.NewGuid()), null).Single();
        envelope.Destination = _queue.Uri;
        envelope.ScheduledTime = scheduledTime;
        await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, runtime.DurabilitySettings.AssignedNodeNumber);
        envelope.WasPersistedInOutbox = true;
        return envelope;
    }

    private async Task<int> outgoingCountAsync() => (await _host.GetRuntime().Storage.Admin.AllOutgoingAsync()).Count;

    private Task theOutboxDeleteFailsAsync() => executeAsync(
        "CREATE TRIGGER fail_outbox_deletes BEFORE DELETE ON wolverine_outgoing_envelopes " +
        "BEGIN SELECT RAISE(ABORT, 'the outbox delete fails'); END");

    [Fact]
    public async Task a_send_moves_the_envelope_from_the_outbox_to_the_queue()
    {
        var envelope = await persistedInTheOutboxAsync();

        await _queue.SendAsync(envelope);

        (await _queue.CountAsync()).ShouldBe(1);
        (await outgoingCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task a_failed_outbox_delete_leaves_nothing_in_the_queue()
    {
        var envelope = await persistedInTheOutboxAsync();
        await theOutboxDeleteFailsAsync();

        await Should.ThrowAsync<SqliteException>(async () => await _queue.SendAsync(envelope));

        // Still only in the outbox, so recovery sends it exactly once
        (await _queue.CountAsync()).ShouldBe(0);
        (await outgoingCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task a_failed_outbox_delete_leaves_nothing_in_the_scheduled_table()
    {
        var envelope = await persistedInTheOutboxAsync(DateTimeOffset.UtcNow.AddHours(1));
        await theOutboxDeleteFailsAsync();

        await Should.ThrowAsync<SqliteException>(async () => await _queue.SendAsync(envelope));

        (await _queue.ScheduledCountAsync()).ShouldBe(0);
        (await outgoingCountAsync()).ShouldBe(1);
    }
}

public record SqliteOutboxMoveMessage(Guid Id);
