using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;

namespace PostgresqlTests.Transport;

/// <summary>
/// https://github.com/JasperFx/wolverine/issues/5107. An envelope whose outbox row was written by something
/// other than the RDBMS message store -- the Marten or EF Core outbox, or outbox recovery reading the row back
/// -- does not carry <see cref="Envelope.WasPersistedInOutbox"/>, so the PostgreSQL queue sender inserted it
/// into the queue in one transaction and left the outbox row for DurableSendingAgent to delete in a second
/// one. A node that died between the two left a row that outbox recovery sent again, and once the receiver's
/// handled inbox row had expired the copy was handled a second time.
/// </summary>
public class Bug_5107_outbox_row_survives_the_queue_insert : IAsyncLifetime
{
    private const string SchemaName = "gh5107";
    private const string QueueName = "gh5107";

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
        }

        Gh5107Handler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5107Handler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName,
                        transportSchema: SchemaName)
                    .AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<Gh5107Message>().ToPostgresqlQueue(QueueName);
                opts.ListenToPostgresqlQueue(QueueName).UseDurableInbox();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private PostgresqlQueue theQueue =>
        _host.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>().Queues[QueueName];

    private async Task<Envelope> storeInTheOutboxAsync(Gh5107Message message, int ownerId)
    {
        var runtime = _host.GetRuntime();
        var envelope = runtime.RoutingFor(typeof(Gh5107Message)).RouteForSend(message, null).Single();

        await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, ownerId);

        // What the Marten and EF Core outboxes, and outbox recovery, hand the sender: the row is there,
        // the flag is not
        envelope.WasPersistedInOutbox = false;

        return envelope;
    }

    private async Task<long> countAsync(string sql, Guid id)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        var count = await conn.CreateCommand(sql).With("id", id).ExecuteScalarAsync();
        return (long)count!;
    }

    private Task<long> outboxCountAsync(Guid id) =>
        countAsync($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable} where id = :id", id);

    private Task<long> queueCountAsync(Guid id) =>
        countAsync($"select count(*) from {theQueue.QueueTable.Identifier} where id = :id", id);

    private async Task executeAsync(string sql, Guid id)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand(sql).With("id", id).ExecuteNonQueryAsync();
    }

    private static async Task waitForAsync(Func<Task<bool>> condition, string description)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeout)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for {description}");
    }

    [Fact]
    public async Task the_queue_insert_takes_the_outbox_row_with_it()
    {
        // Owned by a node that is not this one, so neither this node's sending agent nor recovery touches it
        var envelope = await storeInTheOutboxAsync(new Gh5107Message(Guid.NewGuid()), 9999);

        await ((ISender)new PostgresqlQueueSender(theQueue)).SendAsync(envelope);

        // Nothing has deleted the outbox row but the send itself. Before the fix the row was still there,
        // waiting for a delete in a separate transaction that a dying node never ran
        (await outboxCountAsync(envelope.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task an_envelope_with_no_outbox_row_is_still_written_to_the_queue()
    {
        var message = new Gh5107Message(Guid.NewGuid());
        var envelope = _host.GetRuntime().RoutingFor(typeof(Gh5107Message)).RouteForSend(message, null).Single();

        await ((ISender)new PostgresqlQueueSender(theQueue)).SendAsync(envelope);

        await waitForAsync(() => Task.FromResult(Gh5107Handler.CountFor(message.Id) == 1), "the handling");
    }

    [Fact]
    public async Task a_scheduled_send_takes_the_outbox_row_with_it()
    {
        var envelope = await storeInTheOutboxAsync(new Gh5107Message(Guid.NewGuid()), 9999);
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);

        await ((ISender)new PostgresqlQueueSender(theQueue)).SendAsync(envelope);

        (await outboxCountAsync(envelope.Id)).ShouldBe(0);
        (await countAsync($"select count(*) from {theQueue.ScheduledTable.Identifier} where id = :id", envelope.Id))
            .ShouldBe(1);
    }

    [Fact]
    public async Task an_envelope_is_handled_once_when_the_sender_dies_after_the_queue_insert()
    {
        var message = new Gh5107Message(Guid.NewGuid());
        var envelope = await storeInTheOutboxAsync(message, 9999);

        // The queue insert commits, then the "node" dies before DurableSendingAgent deletes its outbox row
        await ((ISender)new PostgresqlQueueSender(theQueue)).SendAsync(envelope);

        await waitForAsync(() => Task.FromResult(Gh5107Handler.CountFor(message.Id) == 1), "the first handling");

        // The receiver's handled row expires after KeepAfterMessageHandling
        await waitForAsync(
            async () => await countAsync(
                $"select count(*) from {SchemaName}.{DatabaseConstants.IncomingTable} where id = :id and status = 'Handled'",
                envelope.Id) == 1, "the handled inbox row");
        await executeAsync($"delete from {SchemaName}.{DatabaseConstants.IncomingTable} where id = :id", envelope.Id);

        // The orphan sweep hands whatever the dead node still owned to outbox recovery
        await executeAsync($"update {SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = 0 where id = :id",
            envelope.Id);

        // Give outbox recovery (250ms polling) and the listener plenty of time to send and handle a copy
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        (await outboxCountAsync(envelope.Id)).ShouldBe(0);
        (await queueCountAsync(envelope.Id)).ShouldBe(0);
        Gh5107Handler.CountFor(message.Id).ShouldBe(1);
    }
}

public record Gh5107Message(Guid Id);

public static class Gh5107Handler
{
    private static readonly LightweightCache<Guid, int> _counts = new(_ => 0);
    private static readonly object _lock = new();

    public static void Reset()
    {
        lock (_lock) _counts.Clear();
    }

    public static int CountFor(Guid id)
    {
        lock (_lock) return _counts[id];
    }

    public static void Handle(Gh5107Message message)
    {
        lock (_lock) _counts[message.Id] = _counts[message.Id] + 1;
    }
}
