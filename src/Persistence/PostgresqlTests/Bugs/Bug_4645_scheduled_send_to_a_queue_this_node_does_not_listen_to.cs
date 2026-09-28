using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Postgresql.Transport;
using Wolverine.Tracking;

namespace PostgresqlTests.Bugs;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4645, reported against 6.40.0 on a
/// two-node Balanced cluster with a global partitioned topology over sharded PostgreSQL queues.
///
/// <para>
/// A handler on the node that does NOT own a queue returned a cascading message delayed three seconds
/// and addressed to that queue. It was never handled, nothing was logged and nothing was dead lettered.
/// </para>
///
/// <para>
/// A scheduled envelope sent from inside a handler parks in this node's <em>inbox</em> under its eventual
/// destination -- <c>IEnvelopeTransaction.PersistAsync</c> routes <c>Scheduled</c> to
/// <c>PersistIncomingAsync</c> even for a remote destination, which is how the durable inbox doubles as
/// the scheduler. When it comes due the poller promotes the row to <c>Incoming</c>, keeps
/// <c>received_at</c> pointed at the queue, and hands it to <c>EnqueueDirectlyAsync</c>. This node has no
/// listener for that queue, so the envelope goes out through a sending agent and into the queue table --
/// and, before the fix, the inbox row was left behind as <c>Incoming</c> forever.
/// </para>
///
/// <para>
/// That leftover row is what kills the message. The owning node's listener runs an anti-duplicate probe
/// before every pop (GH-4316) that deletes any queue row whose id is already in the inbox at that queue's
/// address, with no status filter -- so it deletes the row this node just wrote, on its very next poll.
/// </para>
///
/// <para>
/// Single-host on purpose: one host sending to a PostgreSQL queue it does not listen to is the entire
/// precondition, and it reproduces without a two-node rig, agent assignment or a failover. The
/// <em>consequence</em> is then asserted directly against the probe, since the leftover row and the probe
/// are all it takes.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class Bug_4645_scheduled_send_to_a_queue_this_node_does_not_listen_to : IAsyncLifetime
{
    private const string SchemaName = "gh4645";
    private const string QueueName = "gh4645orders";

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // The default is a 5 second poll behind a randomized first execution, which would make this
                // test mostly a stopwatch.
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName,
                        transportSchema: SchemaName + "_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                // Deliberately NO ListenToPostgresqlQueue(QueueName). This host publishes to a queue that,
                // in the reported topology, another node holds the exclusive listener agent for.
                opts.PublishMessage<Gh4645Check>().ToPostgresqlQueue(QueueName);

                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh4645StartHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        // Both assertions are absolute row counts, and AutoPurgeOnStartup does not reach the inbox rows
        // this test is about -- the stranded row is precisely the one nothing cleans up. Empty both tables
        // by hand so a previous run cannot make this pass or fail for the wrong reason.
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await conn.CreateCommand(
                $"delete from {SchemaName}.{DatabaseConstants.IncomingTable}; delete from {theQueue.QueueTable.Identifier};")
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_inbox_row_is_retired_once_the_envelope_reaches_the_queue()
    {
        await _host.MessageBus().InvokeAsync(new Gh4645Start(Guid.NewGuid().ToString()),
            TestContext.Current.CancellationToken);

        // The delayed envelope parks in the inbox under the queue's address, so it is already there before
        // the scheduled poll runs. Without this the assertion below could pass vacuously against a message
        // that was never scheduled at all.
        (await countInboxRowsAsync()).ShouldBe(1);

        await waitForQueueDepthAsync(1);

        // The fix. Before it this stayed at 1 forever.
        (await countInboxRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task the_queue_row_survives_the_owning_nodes_anti_duplicate_probe()
    {
        await _host.MessageBus().InvokeAsync(new Gh4645Start(Guid.NewGuid().ToString()),
            TestContext.Current.CancellationToken);

        await waitForQueueDepthAsync(1);

        // The GH-4316 statement the owning node's listener runs ahead of every pop, verbatim, against the
        // same tables. Before the fix the leftover inbox row matched and took the queue row with it -- the
        // message the reporter never saw.
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await conn
            .CreateCommand(
                $"delete from {theQueue.QueueTable.Identifier} where id in " +
                $"(select id from {SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.ReceivedAt} = '{theQueue.Uri}')")
            .ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await conn.CloseAsync();

        (await theQueue.CountAsync()).ShouldBe(1);
    }

    private PostgresqlQueue theQueue =>
        _host.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>().Queues[QueueName];

    private async Task waitForQueueDepthAsync(long expected)
    {
        // Bounded poll rather than a sleep: the scheduled poll and the send are both asynchronous, and what
        // the test is about is what is left behind once they are done, not how long they took.
        using var timeout = new CancellationTokenSource(30.Seconds());
        while (!timeout.IsCancellationRequested)
        {
            if (await theQueue.CountAsync() >= expected) return;

            try
            {
                await Task.Delay(100.Milliseconds(), timeout.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException(
            $"The scheduled envelope never reached {QueueName}; depth is {await theQueue.CountAsync()}, expected {expected}");
    }

    private async Task<long> countInboxRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var count = (long)(await conn
            .CreateCommand(
                $"select count(*) from {SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.ReceivedAt} = '{theQueue.Uri}'")
            .ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        await conn.CloseAsync();

        return count;
    }
}

public record Gh4645Start(string OrderId);

public record Gh4645Check(string OrderId);

public static class Gh4645StartHandler
{
    // The reported shape: a cascading message returned from a handler, delayed, addressed to a queue this
    // node does not listen to. It has to go through a transaction -- a bare bus.ScheduleAsync outside a
    // handler writes an outbox row and takes the queue's own scheduled table, which was never broken.
    [Transactional]
    public static DeliveryMessage<Gh4645Check> Handle(Gh4645Start command)
    {
        return new Gh4645Check(command.OrderId).DelayedFor(1.Seconds());
    }
}
