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
/// A scheduled retry of a message received on a SQLite queue deletes its inbox row and writes it to the
/// queue's scheduled table. Those were two autocommitted statements in one command, so a scheduled write that failed -- a command
/// timeout or a deadlock, found by the durability chaos rig -- left the message in neither table, alive only in
/// the receiver's in-memory retry, and a node that died or shut down before that retry succeeded lost it.
/// </summary>
public class scheduled_retry_is_atomic : IAsyncLifetime
{
    private const string QueueName = "schedretry";

    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(scheduled_retry_is_atomic));
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
                opts.PublishMessage<SqliteScheduledRetryMessage>().ToSqliteQueue(QueueName);
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

    /// <summary>
    /// What a durable listener has done by the time the handler fails: the envelope sits in the inbox
    /// </summary>
    private async Task<Envelope> receivedIntoTheInboxAsync(int? ownerId = null)
    {
        var runtime = _host.GetRuntime();
        var envelope = runtime.RoutingFor(typeof(SqliteScheduledRetryMessage))
            .RouteForSend(new SqliteScheduledRetryMessage(Guid.NewGuid()), null).Single();
        envelope.Destination = _queue.Uri;
        // Owned by the node that popped it, as the durable pop writes it
        envelope.OwnerId = ownerId ?? runtime.DurabilitySettings.AssignedNodeNumber;
        await runtime.Storage.Inbox.StoreIncomingAsync(envelope);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
        return envelope;
    }

    private async Task<bool> isInTheInboxAsync(Envelope envelope) =>
        (await _host.GetRuntime().Storage.Admin.AllIncomingAsync()).Any(x => x.Id == envelope.Id);

    private async Task executeAsync(string sql)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task a_scheduled_retry_moves_the_inbox_row_to_the_scheduled_table()
    {
        var envelope = await receivedIntoTheInboxAsync();

        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await isInTheInboxAsync(envelope)).ShouldBeFalse();
        (await _queue.ScheduledCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task a_failed_scheduled_write_leaves_the_inbox_row_in_place()
    {
        var envelope = await receivedIntoTheInboxAsync();

        await executeAsync(
            $"CREATE TRIGGER fail_scheduled_writes BEFORE INSERT ON {_queue.ScheduledTable.Identifier} " +
            "BEGIN SELECT RAISE(ABORT, 'the scheduled write fails'); END;");

        await Should.ThrowAsync<SqliteException>(() => _queue.ScheduleRetryAsync(envelope, CancellationToken.None));

        // Still in the inbox, so recovery runs it again if this node goes away
        (await isInTheInboxAsync(envelope)).ShouldBeTrue();
        (await _queue.ScheduledCountAsync()).ShouldBe(0);

        // and the receiver's retry completes the move once the write can succeed
        await executeAsync("DROP TRIGGER fail_scheduled_writes");
        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await isInTheInboxAsync(envelope)).ShouldBeFalse();
        (await _queue.ScheduledCountAsync()).ShouldBe(1);
    }

    // GH-5426. A node ejected as stale resumes after another node recovered the inbox row, fails the envelope again
    // and asks for the retry too. It must neither take the other execution's row nor write a second scheduled copy
    [Fact]
    public async Task a_node_that_no_longer_owns_the_inbox_row_schedules_nothing()
    {
        var envelope = await receivedIntoTheInboxAsync(ownerId: 99);

        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await isInTheInboxAsync(envelope)).ShouldBeTrue();
        (await _queue.ScheduledCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task the_node_that_owns_the_inbox_row_moves_it_to_the_scheduled_table()
    {
        var envelope = await receivedIntoTheInboxAsync(ownerId: _host.GetRuntime().DurabilitySettings.AssignedNodeNumber);

        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await isInTheInboxAsync(envelope)).ShouldBeFalse();
        (await _queue.ScheduledCountAsync()).ShouldBe(1);
    }
}

public record SqliteScheduledRetryMessage(Guid Id);
