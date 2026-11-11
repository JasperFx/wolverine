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
/// GH-5438, found by the durability chaos rig. Sending an outboxed envelope to a durable MySQL queue moves it from the
/// outbox to the queue table: an INSERT and then a DELETE, sent as one command. MySQL committed each statement on its
/// own, so a process killed between them, or a command timeout, left the envelope in the queue AND in the outbox.
/// Outbox recovery then sent it a second time. When the first copy had gone on to a scheduled retry, which deletes its
/// inbox row, nothing recognised the second copy as a duplicate, and the message was handled twice.
/// </summary>
public class outbox_to_queue_move_is_atomic : IAsyncLifetime
{
    private const string SchemaName = "wolverine_outboxmove";
    private const string QueueName = "outboxmove";

    private IHost _host = null!;
    private MySqlQueue _queue = null!;

    public async ValueTask InitializeAsync()
    {
        await executeAsync($"DROP DATABASE IF EXISTS {SchemaName}; CREATE DATABASE {SchemaName}");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, SchemaName,
                    transportSchema: SchemaName);

                // Declared without a listener, so nothing but the test touches the queue
                opts.PublishMessage<MySqlOutboxMoveMessage>().ToMySqlQueue(QueueName);
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

    private static async Task executeAsync(string sql)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>What the outbox has done by the time the envelope is flushed to the queue</summary>
    private async Task<Envelope> persistedInTheOutboxAsync(DateTimeOffset? scheduledTime = null)
    {
        var runtime = _host.GetRuntime();
        var envelope = runtime.RoutingFor(typeof(MySqlOutboxMoveMessage))
            .RouteForSend(new MySqlOutboxMoveMessage(Guid.NewGuid()), null).Single();
        envelope.Destination = _queue.Uri;
        envelope.ScheduledTime = scheduledTime;
        await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, runtime.DurabilitySettings.AssignedNodeNumber);
        envelope.WasPersistedInOutbox = true;
        return envelope;
    }

    private async Task<int> outgoingCountAsync() => (await _host.GetRuntime().Storage.Admin.AllOutgoingAsync()).Count;

    private Task theOutboxDeleteFailsAsync() => executeAsync(
        $"CREATE TRIGGER {SchemaName}.fail_outbox_deletes BEFORE DELETE ON {SchemaName}.wolverine_outgoing_envelopes " +
        "FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'the outbox delete fails'");

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

        await Should.ThrowAsync<MySqlException>(async () => await _queue.SendAsync(envelope));

        // Still only in the outbox, so recovery sends it exactly once
        (await _queue.CountAsync()).ShouldBe(0);
        (await outgoingCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task a_failed_outbox_delete_leaves_nothing_in_the_scheduled_table()
    {
        var envelope = await persistedInTheOutboxAsync(DateTimeOffset.UtcNow.AddHours(1));
        await theOutboxDeleteFailsAsync();

        await Should.ThrowAsync<MySqlException>(async () => await _queue.SendAsync(envelope));

        (await _queue.ScheduledCountAsync()).ShouldBe(0);
        (await outgoingCountAsync()).ShouldBe(1);
    }
}

public record MySqlOutboxMoveMessage(Guid Id);
