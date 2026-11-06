using IntegrationTests;
using MySqlConnector;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Tracking;

namespace MySqlTests.Transport;

/// <summary>
/// A scheduled retry of a message received on a MySQL queue deletes its inbox row and writes it to the
/// queue's scheduled table. Those were two autocommitted statements in one command, so a scheduled write that failed -- a command
/// timeout or a deadlock, found by the durability chaos rig -- left the message in neither table, alive only in
/// the receiver's in-memory retry, and a node that died or shut down before that retry succeeded lost it.
/// </summary>
public class scheduled_retry_is_atomic : IAsyncLifetime
{
    private const string SchemaName = "wolverine_schedretry";
    private const string QueueName = "schedretry";

    private IHost _host = null!;
    private MySqlQueue _queue = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS {SchemaName}; CREATE DATABASE {SchemaName}";
            await cmd.ExecuteNonQueryAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, SchemaName,
                    transportSchema: SchemaName);

                // Declared without a listener, so nothing but the test touches the queue
                opts.PublishMessage<MySqlScheduledRetryMessage>().ToMySqlQueue(QueueName);
            }).StartAsync();

        _queue = _host.GetRuntime().Options.Transports.GetOrCreate<MySqlTransport>().Queues[QueueName];
        _queue.Mode = EndpointMode.Durable;
        await _queue.PurgeAsync(NullLogger.Instance);
        await _host.GetRuntime().Storage.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// What a durable listener has done by the time the handler fails: the envelope sits in the inbox
    /// </summary>
    private async Task<Envelope> receivedIntoTheInboxAsync()
    {
        var runtime = _host.GetRuntime();
        var envelope = runtime.RoutingFor(typeof(MySqlScheduledRetryMessage))
            .RouteForSend(new MySqlScheduledRetryMessage(Guid.NewGuid()), null).Single();
        envelope.Destination = _queue.Uri;
        await runtime.Storage.Inbox.StoreIncomingAsync(envelope);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
        return envelope;
    }

    private async Task<bool> isInTheInboxAsync(Envelope envelope) =>
        (await _host.GetRuntime().Storage.Admin.AllIncomingAsync()).Any(x => x.Id == envelope.Id);

    private async Task executeAsync(string sql)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
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
            $"CREATE TRIGGER {SchemaName}.fail_scheduled_writes BEFORE INSERT ON {_queue.ScheduledTable.Identifier} " +
            "FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'the scheduled write fails'");

        await Should.ThrowAsync<MySqlException>(() => _queue.ScheduleRetryAsync(envelope, CancellationToken.None));

        // Still in the inbox, so recovery runs it again if this node goes away
        (await isInTheInboxAsync(envelope)).ShouldBeTrue();
        (await _queue.ScheduledCountAsync()).ShouldBe(0);

        // and the receiver's retry completes the move once the write can succeed
        await executeAsync($"DROP TRIGGER {SchemaName}.fail_scheduled_writes");
        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await isInTheInboxAsync(envelope)).ShouldBeFalse();
        (await _queue.ScheduledCountAsync()).ShouldBe(1);
    }
}

public record MySqlScheduledRetryMessage(Guid Id);
