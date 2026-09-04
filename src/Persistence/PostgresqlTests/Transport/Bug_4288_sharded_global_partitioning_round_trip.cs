using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-4288, the PostgreSQL twin of the SQL Server defect. When a globally partitioned message
/// actually round-trips through a sharded PostgreSQL queue -- no local companion-queue shortcut --
/// the durable dequeue moves the envelope into the inbox, and the GlobalPartitionedReceiverBridge
/// then forwarded it to the companion local queue's DurableReceiver, which stored it AGAIN. Every
/// message threw DuplicateIncomingEnvelopeException and sat permanently parked in
/// wolverine_incoming_envelopes. This test defeats the local shortcut by sending straight to the
/// slot endpoint, forcing the full queue-table round trip.
/// </summary>
public class Bug_4288_sharded_global_partitioning_round_trip : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        B4288PgHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "b4288_pg",
                        transportSchema: "b4288_pg_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(B4288PgHandler));

                opts.MessagePartitioning.ByMessage<IB4288PgMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues("b4288pg", 2);
                    topology.MessagesImplementing<IB4288PgMessage>();
                });
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task messages_sent_through_the_sharded_queue_are_executed_and_do_not_wedge_the_inbox()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        // Send straight to one slot endpoint so the message cannot take the companion local queue
        // shortcut -- it has to go through the sharded queue table and the durable dequeue that
        // moves it into the inbox
        Func<IMessageContext, Task> sendThroughTheShardQueue = async bus =>
        {
            foreach (var id in ids)
            {
                await bus.EndpointFor(new Uri("postgresql://b4288pg1")).SendAsync(new B4288PgWork(id));
            }
        };

        var tracked = await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(120.Seconds())
            .ExecuteAndWaitAsync(sendThroughTheShardQueue);

        // Before the fix nothing was ever executed -- the local companion queue's DurableReceiver
        // stored each envelope a second time and DuplicateIncomingEnvelopeException swallowed it.
        // Scope to this run's ids: startup recovery may also (correctly) execute envelopes a
        // previous failed run left stuck in the inbox.
        tracked.Executed.Envelopes()
            .Count(x => x.Message is B4288PgWork work && ids.Contains(work.Id)).ShouldBe(ids.Length);

        foreach (var id in ids)
        {
            B4288PgHandler.Received.ShouldContain(id);
        }

        // ...and the reported wedge state: rows parked as 'Incoming' that no recovery pass will
        // ever touch because they are owned by a live node. Mark-as-handled can be coalesced, so
        // give the status flip a few seconds rather than asserting one snapshot.
        var cancellation = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(cancellation);

        var stuck = long.MaxValue;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (stuck > 0 && DateTimeOffset.UtcNow < deadline)
        {
            stuck = (long)(await conn
                .CreateCommand(
                    "select count(*) from b4288_pg.wolverine_incoming_envelopes where status = 'Incoming'")
                .ExecuteScalarAsync(cancellation))!;

            if (stuck > 0)
            {
                await Task.Delay(250.Milliseconds(), cancellation);
            }
        }

        stuck.ShouldBe(0);
    }
}

public interface IB4288PgMessage
{
    Guid Id { get; }
}

public record B4288PgWork(Guid Id) : IB4288PgMessage;

public static class B4288PgHandler
{
    public static readonly ConcurrentBag<Guid> Received = new();

    public static void Handle(B4288PgWork message) => Received.Add(message.Id);
}
