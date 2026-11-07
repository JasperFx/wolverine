using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-5426. A scheduled retry of a message received on a PostgreSQL queue deletes its inbox row and writes it to the
/// queue's scheduled table. A node ejected as stale while it held the envelope in memory resumed after another node
/// had recovered the row, failed the envelope again and rescheduled it too: the delete matched only the id, so it
/// took the other execution's inbox row and wrote a second scheduled copy, and both copies ran the retry. The retry
/// is now fenced on the inbox row's owner.
/// </summary>
[Collection("Postgresql")]
public class scheduled_retry_is_fenced_on_the_inbox_owner : IAsyncLifetime
{
    private const string SchemaName = "schedretry";
    private const string QueueName = "schedretry";

    private IHost _host = null!;
    private PostgresqlQueue _queue = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName,
                    transportSchema: SchemaName).AutoProvision();

                // Declared without a listener, so nothing but the test touches the queue
                opts.PublishMessage<FencedRetryMessage>().ToPostgresqlQueue(QueueName);
            }).StartAsync();

        _queue = _host.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>().Queues[QueueName];
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
    /// What a durable pop has done by the time the handler fails: the envelope sits in the inbox, owned by the node
    /// that popped it (or recovered it)
    /// </summary>
    private async Task<Envelope> receivedIntoTheInboxAsync(int ownerId)
    {
        var runtime = _host.GetRuntime();
        var envelope = runtime.RoutingFor(typeof(FencedRetryMessage))
            .RouteForSend(new FencedRetryMessage(Guid.NewGuid()), null).Single();
        envelope.Destination = _queue.Uri;
        envelope.OwnerId = ownerId;
        await runtime.Storage.Inbox.StoreIncomingAsync(envelope);

        // What DurableReceiver does before it asks the queue to schedule the retry
        envelope.OwnerId = TransportConstants.AnyNode;
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
        return envelope;
    }

    private async Task<Envelope?> inboxRowAsync(Envelope envelope) =>
        (await _host.GetRuntime().Storage.Admin.AllIncomingAsync()).SingleOrDefault(x => x.Id == envelope.Id);

    private int thisNode => _host.GetRuntime().DurabilitySettings.AssignedNodeNumber;

    [Fact]
    public async Task the_owner_moves_the_inbox_row_to_the_scheduled_table()
    {
        var envelope = await receivedIntoTheInboxAsync(thisNode);

        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await inboxRowAsync(envelope)).ShouldBeNull();
        (await _queue.ScheduledCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task a_node_that_no_longer_owns_the_inbox_row_schedules_nothing()
    {
        // Recovered by node 99 after this node was ejected as stale
        var envelope = await receivedIntoTheInboxAsync(99);

        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        // The other execution's row is left alone, and no second copy is scheduled
        var row = await inboxRowAsync(envelope);
        row.ShouldNotBeNull();
        row.OwnerId.ShouldBe(99);
        (await _queue.ScheduledCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task a_node_whose_inbox_row_is_gone_schedules_nothing()
    {
        // The other execution already handled it, or scheduled its own retry
        var envelope = await receivedIntoTheInboxAsync(thisNode);
        await _host.GetRuntime().Storage.Admin.ClearAllAsync();

        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await _queue.ScheduledCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task an_envelope_from_a_real_durable_pop_is_fenced_too()
    {
        // The pop does not set WasPersistedInInbox, so the fence cannot depend on it
        var runtime = _host.GetRuntime();
        await _queue.SendAsync(new Envelope(new FencedRetryMessage(Guid.NewGuid()))
        {
            Destination = _queue.Uri, MessageType = "fenced", Data = [1, 2, 3], ContentType = "application/json"
        });

        var receiver = new DurableReceiver(_queue, runtime, Substitute.For<IHandlerPipeline>());
        await using var listener = new PostgresqlQueueListener(_queue, runtime, receiver, _queue.DataSource, null);
        var envelope = (await listener.TryPopDurablyAsync(10, runtime.DurabilitySettings, NullLogger.Instance,
            CancellationToken.None)).Single();

        // This node is ejected as stale and node 99 recovers the row
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"update {SchemaName}.wolverine_incoming_envelopes set owner_id = 99 where id = '{envelope.Id}'";
            (await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await inboxRowAsync(envelope)).ShouldNotBeNull().OwnerId.ShouldBe(99);
        (await _queue.ScheduledCountAsync()).ShouldBe(0);
    }
}

public record FencedRetryMessage(Guid Id);
