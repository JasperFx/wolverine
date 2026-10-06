using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;

namespace PostgresqlTests.Transport;

/// <summary>
/// The slot lifecycle -- handoff, dead letter replay, scheduled retry, requeue -- for messages that reached a
/// global partition slot THROUGH ITS SHARD QUEUE TABLE rather than through <c>GlobalPartitionedRoute</c>'s
/// local shortcut.
///
/// <para>
/// Every regression fixture for GH-4700, GH-4776, GH-4777 and GH-4822 publishes from a Solo host that owns the
/// slot, so every message takes the shortcut onto the companion local queue and every inbox row carries the
/// companion address as its <c>received_at</c>. In a real cluster that is the minority path: a message published
/// by any node that does not own the slot goes into the shard queue table, the owner's listener pops it, and
/// <c>TryPopDurablyAsync</c> writes its inbox row at the <em>slot's</em> address, owned by the popping node. The
/// companion queue then executes it, but the row is not at the companion address -- which is the only address
/// the companion queue's drain releases. The tests here send straight to the slot endpoint (the GH-4288
/// technique) so that every row is of the shard-table shape, and ask the same lifecycle questions the shortcut
/// fixtures already answer.
/// </para>
///
/// <para>
/// A Solo host owns every slot, so stopping one slot's exclusive listener is a genuine, deterministic handoff
/// without the timing lottery of a second node -- the same technique as the fixtures above.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class global_partition_slot_lifecycle_through_the_shard_queue : IAsyncLifetime
{
    private const string Schema = "slotshard";

    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private IMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        ShardPathHandler.Reset();
        ShardPathWitnessHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, Schema,
                        transportSchema: $"{Schema}_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ShardPathHandler))
                    .IncludeType(typeof(ShardPathWitnessHandler));

                // One policy per failure shape, selected by the message itself
                opts.OnException<DeadLetterMeException>().MoveToErrorQueue();
                opts.OnException<RetryMeLaterException>().ScheduleRetry(2.Seconds());
                opts.OnException<RequeueMeException>().Requeue();

                opts.MessagePartitioning.ByMessage<ShardPathMessage>(x => x.GroupId.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues("shardpath", 2);
                    topology.Message<ShardPathMessage>();
                });

                // A durable local queue with no part in the topology: the liveness witness for the negative
                // assertions below, exactly as in the GH-4776 fixture.
                opts.LocalQueue("shardpath-witness").UseDurableInbox();
                opts.PublishMessage<ShardPathWitness>().ToLocalQueue("shardpath-witness");
            }).StartAsync();

        _runtime = _host.GetRuntime();
        _store = _host.Services.GetRequiredService<IMessageStore>();

        // AutoPurgeOnStartup purges the queue tables only. Leftover inbox rows from an earlier run would be
        // claimed by the slot's recovery loops the moment the listeners start and executed into
        // ShardPathHandler.Received, so every count in this fixture would be wrong -- see the GH-4777 fixture.
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        foreach (var table in new[] { "wolverine_incoming_envelopes", "wolverine_dead_letters", "wolverine_outgoing_envelopes" })
        {
            var delete = conn.CreateCommand();
            delete.CommandText = $"delete from {Schema}.{table}";
            await delete.ExecuteNonQueryAsync();
        }

        await conn.CloseAsync();

        ShardPathHandler.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        ShardPathHandler.Reset();
        await _host.StopAsync();
        _host.Dispose();
    }

    private PostgresqlQueue[] theSlots()
    {
        var transport = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        return [transport.Queues["shardpath1"], transport.Queues["shardpath2"]];
    }

    /// <summary>
    /// Straight to the slot endpoint, bypassing GlobalPartitionedRoute, so the message round-trips through the
    /// shard queue table and its inbox row is written by the pop at the slot's address. The GH-4288 technique.
    /// </summary>
    private Task sendThroughTheShardQueueAsync(PostgresqlQueue slot, ShardPathMessage message)
    {
        return _host.MessageBus().EndpointFor(slot.Uri).SendAsync(message).AsTask();
    }

    /// <summary>
    /// A group id whose messages take GlobalPartitionedRoute's local shortcut onto the given slot's companion
    /// queue. Asked of the router rather than computed, so the test cannot drift from the hashing.
    /// </summary>
    private Guid groupIdLandingOn(Uri companion)
    {
        var bus = _host.MessageBus();

        for (var i = 0; i < 500; i++)
        {
            var candidate = Guid.NewGuid();
            var destination = bus.PreviewSubscriptions(new ShardPathMessage(candidate, 0, null)).Single().Destination;
            if (destination == companion) return candidate;
        }

        throw new TimeoutException($"Could not find a group id routing to {companion}");
    }

    private async Task<string> describeInboxAsync()
    {
        var rows = await _runtime.Storage.Admin.AllIncomingAsync();
        return rows.Select(x => $"{x.Id}@{x.Destination} {x.Status} owner={x.OwnerId}").Join("; ");
    }

    private async Task<Envelope?> inboxRowAsync(Guid envelopeId)
    {
        var all = await _runtime.Storage.Admin.AllIncomingAsync();
        return all.FirstOrDefault(x => x.Id == envelopeId);
    }

    /// <summary>
    /// The GH-4777 question for the shard-table path. When the slot is given up, the backlog the companion
    /// queue was holding has to be finished or released -- and "released" means owner 0, where the next
    /// owner's recovery loop can see it. A row left Incoming and owned by this still-live node is invisible to
    /// every recovery sweep there is: the orphan sweep only touches dead owners, and the slot's own loop only
    /// loads owner 0. Nothing would ever run it until this node stopped.
    /// </summary>
    [Fact]
    public async Task losing_the_slot_releases_a_backlog_that_arrived_through_the_shard_queue()
    {
        var slot = theSlots()[0];
        var groupId = Guid.NewGuid();

        const int total = 14;
        for (var i = 1; i <= total; i++)
        {
            await sendThroughTheShardQueueAsync(slot, new ShardPathMessage(groupId, i, null));
        }

        await waitForAsync(() => ShardPathHandler.Handled.Count >= 2,
            () => $"The backlog never started. handled={ShardPathHandler.Handled.Count}");

        // Precondition for the whole fixture: these rows are of the shard-table shape, not the companion's
        ShardPathHandler.Received.First().Destination.ShouldBe(slot.Uri);

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        var atStop = ShardPathHandler.Handled.Count;
        atStop.ShouldBeLessThan(total,
            "Precondition: the queue has to still have a backlog when the slot is given up, or this test " +
            "proves nothing. Lower the handler delay or raise the message count if this trips.");

        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        ShardPathHandler.Handled.Count.ShouldBe(atStop,
            $"A node that has given up the slot must not execute any more of its messages. Handled {atStop} " +
            $"at the stop and {ShardPathHandler.Handled.Count} two seconds later");

        // Nothing may be left owned by this live node: that is a row no sweep will ever look at.
        var inbox = await _runtime.Storage.Admin.AllIncomingAsync();
        var stranded = inbox
            .Where(x => x.Status == EnvelopeStatus.Incoming && x.OwnerId == _runtime.DurabilitySettings.AssignedNodeNumber)
            .ToArray();

        stranded.ShouldBeEmpty(
            "Rows still owned by the ex-owner after it gave the slot up. The orphan sweep ignores live owners " +
            "and the slot's recovery loop only loads owner 0, so these would never run until this node stops: " +
            stranded.Select(x => $"{x.Id}@{x.Destination}").Join(", "));

        // "Finish or release": the remainder is back at owner 0 at the slot's address, where the next owner's
        // own inbox recovery loop (GH-3590) will find it.
        var dormant = await _store.LoadPageOfGloballyOwnedIncomingAsync(slot.Uri, total * 2);
        (atStop + dormant.Count).ShouldBeGreaterThanOrEqualTo(total,
            "Every message should be either executed or released at the slot address");
    }

    /// <summary>
    /// The other half: re-acquiring the slot has to finish the backlog. If the rows were stranded under this
    /// node's own id, the restarted slot's recovery loop cannot see them and this waits out its timeout.
    /// </summary>
    [Fact]
    public async Task re_acquiring_the_slot_finishes_a_backlog_that_arrived_through_the_shard_queue()
    {
        var slot = theSlots()[0];
        var groupId = Guid.NewGuid();

        const int total = 14;
        for (var i = 1; i <= total; i++)
        {
            await sendThroughTheShardQueueAsync(slot, new ShardPathMessage(groupId, i, null));
        }

        await waitForAsync(() => ShardPathHandler.Handled.Count >= 2,
            () => $"The backlog never started. handled={ShardPathHandler.Handled.Count}");

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);
        ShardPathHandler.Handled.Count.ShouldBeLessThan(total, "Precondition: a backlog has to be left over");

        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        // Completeness, not uniqueness: a duplicate at the drain boundary is at-least-once doing what it
        // promises (see the GH-4777 fixture). Every number has to arrive.
        var expected = Enumerable.Range(1, total).ToArray();
        await waitForAsync(() => expected.All(n => ShardPathHandler.Handled.Contains(n)),
            async () => "The backlog was never finished after the slot came back. missing=" +
                        $"[{expected.Where(n => !ShardPathHandler.Handled.Contains(n)).Select(n => n.ToString()).Join(", ")}] inbox: {await describeInboxAsync()}");

        ShardPathHandler.Violations.ShouldBeEmpty("Two messages of one group id ran concurrently");
    }

    /// <summary>
    /// GH-4776 for the shard-table path. A message that failed after arriving through the shard queue carries
    /// the slot's address on its dead letter row, so its replay lands as a dormant row at the slot address.
    /// That is GH-3590's territory rather than GH-4776's -- the slot is a single node listener -- but nothing
    /// in the partitioning fixtures pins it, and it is the shape every dead letter in a real cluster takes
    /// unless the publisher happened to own the slot.
    /// </summary>
    [Fact]
    public async Task a_dead_letter_from_the_shard_queue_is_replayed_only_by_the_slots_owner()
    {
        var slot = theSlots()[0];
        var groupId = Guid.NewGuid();
        ShardPathHandler.FailFirstExecution.TryAdd(groupId, true);

        await sendThroughTheShardQueueAsync(slot, new ShardPathMessage(groupId, 1, "DeadLetter"));

        await waitForAsync(() => ShardPathHandler.Received.Any(x => x.GroupId == groupId),
            () => "The first attempt never ran");

        var first = ShardPathHandler.Received.Single(x => x.GroupId == groupId);
        first.Destination.ShouldBe(slot.Uri, "Precondition: the dead letter row has to carry the slot's address");

        // Stopping the listener drains the move-to-errors block, so the dead letter row exists when this returns
        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        await _store.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync([first.EnvelopeId]);

        // The witness proves the durability agent is awake and claiming dormant rows during the very window in
        // which it leaves this one alone -- a seeded owner-0 row on a plain durable local queue that only the
        // agent would ever recover (the GH-4776 technique).
        var witness = await seedDormantWitnessRowAsync();
        await waitForAsync(() => ShardPathWitnessHandler.Received.Contains(witness),
            () => "The witness row was never recovered, so the durability agent's behaviour was never observed");

        // And the replayed row has to be visible before the negative assertion means anything
        await waitForAsync(async () => (await inboxRowAsync(first.EnvelopeId)) is { Status: EnvelopeStatus.Incoming },
            async () => $"The dead letter was never moved back to the inbox. inbox: {await describeInboxAsync()}");

        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        ShardPathHandler.Received.Count(x => x.GroupId == groupId)
            .ShouldBe(1, "A node that does not own the slot must not execute the replayed message");

        var row = (await inboxRowAsync(first.EnvelopeId)).ShouldNotBeNull();
        row.OwnerId.ShouldBe(TransportConstants.AnyNode, "The replayed row has to stay dormant for the owner");
        row.Destination.ShouldBe(slot.Uri);

        // Deferred, not stranded: give the slot back and the owner runs it through the companion queue
        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        await waitForAsync(() => ShardPathHandler.Received.Count(x => x.GroupId == groupId) == 2,
            async () => $"The replayed dead letter never ran on the owner. inbox: {await describeInboxAsync()}");

        ShardPathHandler.Received.Where(x => x.GroupId == groupId).ShouldAllBe(x => x.Destination == slot.Uri);
    }

    /// <summary>
    /// GH-4822 through the real ScheduleRetry policy rather than a hand-seeded row. The retry of a message that
    /// arrived through the shard queue is parked Scheduled at the slot's address; when it comes due after the
    /// slot moved, the ex-owner's poller has to forward it to the slot rather than run it or strand it.
    /// </summary>
    [Fact]
    public async Task a_scheduled_retry_from_the_shard_queue_is_forwarded_by_an_ex_owner_and_runs_once_on_the_next_owner()
    {
        var slot = theSlots()[0];
        var groupId = Guid.NewGuid();
        ShardPathHandler.FailFirstExecution.TryAdd(groupId, true);

        await sendThroughTheShardQueueAsync(slot, new ShardPathMessage(groupId, 1, "ScheduleRetry"));

        await waitForAsync(() => ShardPathHandler.Received.Any(x => x.GroupId == groupId),
            () => "The first attempt never ran");

        var first = ShardPathHandler.Received.Single(x => x.GroupId == groupId);
        first.Destination.ShouldBe(slot.Uri);

        // The drain flushes the schedule block, so the row is Scheduled at the slot address when this returns.
        // The 2 second retry delay is what keeps it from coming due before the listener is gone.
        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        var scheduled = (await inboxRowAsync(first.EnvelopeId)).ShouldNotBeNull("The retry was never scheduled");
        scheduled.Status.ShouldBe(EnvelopeStatus.Scheduled);
        scheduled.Destination.ShouldBe(slot.Uri);

        // Forwarded: the ex-owner's poller promotes it, sees it does not own the slot, and hands it to the slot
        // queue -- retiring the inbox row on the way (GH-4645/GH-4824).
        await waitForAsync(async () => await inboxRowAsync(first.EnvelopeId) == null,
            async () => $"The due retry was never forwarded off this ex-owner. inbox: {await describeInboxAsync()}");

        ShardPathHandler.Received.Count(x => x.GroupId == groupId).ShouldBe(1,
            "The ex-owner must not run the retry itself");

        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        await waitForAsync(() => ShardPathHandler.Received.Count(x => x.GroupId == groupId) == 2,
            async () => $"The forwarded retry never ran on the owner. inbox: {await describeInboxAsync()}");

        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        ShardPathHandler.Received.Count(x => x.GroupId == groupId).ShouldBe(2, "Exactly one retry");
        ShardPathHandler.Received.Where(x => x.GroupId == groupId).ShouldAllBe(x => x.Destination == slot.Uri);
    }

    /// <summary>
    /// GH-4700 through the real ScheduleRetry policy: the same question for a message that took the local
    /// shortcut, so its retry is parked at the companion address instead.
    /// </summary>
    [Fact]
    public async Task a_scheduled_retry_from_the_companion_queue_is_forwarded_by_an_ex_owner_and_runs_once_on_the_next_owner()
    {
        var slot = theSlots()[0];
        var companion = slot.GlobalPartitionLocalQueueUri!;
        var groupId = groupIdLandingOn(companion);
        ShardPathHandler.FailFirstExecution.TryAdd(groupId, true);

        await _host.MessageBus().PublishAsync(new ShardPathMessage(groupId, 1, "ScheduleRetry"));

        await waitForAsync(() => ShardPathHandler.Received.Any(x => x.GroupId == groupId),
            () => "The first attempt never ran");

        var first = ShardPathHandler.Received.Single(x => x.GroupId == groupId);
        first.Destination.ShouldBe(companion, "Precondition: the first attempt took the local shortcut");

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        var scheduled = (await inboxRowAsync(first.EnvelopeId)).ShouldNotBeNull("The retry was never scheduled");
        scheduled.Status.ShouldBe(EnvelopeStatus.Scheduled);
        scheduled.Destination.ShouldBe(companion);

        await waitForAsync(async () => await inboxRowAsync(first.EnvelopeId) == null,
            async () => $"The due retry was never forwarded off this ex-owner. inbox: {await describeInboxAsync()}");

        ShardPathHandler.Received.Count(x => x.GroupId == groupId).ShouldBe(1,
            "The ex-owner must not run the retry itself");

        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        await waitForAsync(() => ShardPathHandler.Received.Count(x => x.GroupId == groupId) == 2,
            async () => $"The forwarded retry never ran on the owner. inbox: {await describeInboxAsync()}");

        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        ShardPathHandler.Received.Count(x => x.GroupId == groupId).ShouldBe(2, "Exactly one retry");

        // Re-addressed to the slot by the forward (GH-4700), so the owner receives the retry through the shard
        // queue: one execution at each address. (ConcurrentBag does not enumerate in insertion order, so this
        // is asked as a set rather than as first/last.)
        ShardPathHandler.Received.Where(x => x.GroupId == groupId).Select(x => x.Destination).ToArray()
            .ShouldBe([companion, slot.Uri], ignoreOrder: true,
                $"received=[{ShardPathHandler.Describe()}] inbox: {await describeInboxAsync()}");
    }

    /// <summary>
    /// Requeue is the one retry shape that never leaves the node: the companion queue's receiver re-posts the
    /// envelope to itself. Both attempts have to run on the owner, in the companion queue, at the address the
    /// message arrived on, and the row has to end up settled -- for both ways in.
    /// </summary>
    [Fact]
    public async Task a_requeued_message_retries_on_the_owner_and_settles_its_row_for_both_ways_in()
    {
        var slot = theSlots()[0];
        var companion = slot.GlobalPartitionLocalQueueUri!;

        var viaShortcut = groupIdLandingOn(companion);
        var viaShardQueue = Guid.NewGuid();
        ShardPathHandler.FailFirstExecution.TryAdd(viaShortcut, true);
        ShardPathHandler.FailFirstExecution.TryAdd(viaShardQueue, true);

        await _host.MessageBus().PublishAsync(new ShardPathMessage(viaShortcut, 1, "Requeue"));
        await sendThroughTheShardQueueAsync(slot, new ShardPathMessage(viaShardQueue, 1, "Requeue"));

        await waitForAsync(() => ShardPathHandler.Received.Count(x => x.GroupId == viaShortcut) == 2
                                 && ShardPathHandler.Received.Count(x => x.GroupId == viaShardQueue) == 2,
            () => $"The requeued attempts never ran. received=[{ShardPathHandler.Describe()}]");

        ShardPathHandler.Received.Where(x => x.GroupId == viaShortcut).ShouldAllBe(x => x.Destination == companion);
        ShardPathHandler.Received.Where(x => x.GroupId == viaShardQueue).ShouldAllBe(x => x.Destination == slot.Uri);

        // Deliberately "two executions with increasing attempt numbers" rather than exactly [1, 2]. The shard
        // queue path reports attempts 1 and 3: Executor increments Attempts on every execution AND
        // DurableReceiver.DeferAsync increments it again for any envelope not sent by a DurableLocalQueue, so a
        // requeue from an external durable listener double-counts (the shortcut path, sent by the companion
        // DurableLocalQueue, does not). That is a general requeue defect, not a partitioning one, and is reported
        // separately rather than pinned here.
        foreach (var group in new[] { viaShortcut, viaShardQueue })
        {
            var attempts = ShardPathHandler.Received.Where(x => x.GroupId == group).Select(x => x.Attempts).OrderBy(x => x).ToArray();
            attempts.Length.ShouldBe(2);
            attempts[0].ShouldBe(1);
            attempts[1].ShouldBeGreaterThan(1);
        }

        // Settled: nothing left Incoming at either address
        await waitForAsync(async () =>
            {
                var rows = await _runtime.Storage.Admin.AllIncomingAsync();
                return rows.All(x => x.Status != EnvelopeStatus.Incoming);
            },
            async () => $"A requeued message left its row unsettled. inbox: {await describeInboxAsync()}");
    }

    /// <summary>
    /// Back pressure on a slot is decided from <c>ListeningAgent.QueueCount</c>, which reads the receiver's
    /// depth. The slot's receiver is the bridge, and the bridge hands everything to the companion queue -- so
    /// unless it reports the companion's depth, the slot's <c>BackPressureAgent</c> sees a constant 0, never
    /// stops the listener popping the shard queue, and the companion queue grows without bound. GH-4186 fixed
    /// exactly this for the interceptor wrapper; the bridge is the other receiver indirection on the same path.
    /// </summary>
    [Fact]
    public async Task the_slot_listener_reports_the_companion_queues_backlog_as_its_queue_depth()
    {
        var slot = theSlots()[0];
        var groupId = Guid.NewGuid();

        // Slow enough that the backlog is still there when the depth is read
        ShardPathHandler.Delay = 400.Milliseconds();

        const int total = 10;
        for (var i = 1; i <= total; i++)
        {
            await sendThroughTheShardQueueAsync(slot, new ShardPathMessage(groupId, i, null));
        }

        await waitForAsync(() => ShardPathHandler.Handled.Count >= 1,
            () => "The backlog never started");

        var agent = _runtime.Endpoints.FindListeningAgent(slot.Uri).ShouldNotBeNull();

        ShardPathHandler.Handled.Count.ShouldBeLessThan(total - 2,
            "Precondition: a backlog has to exist when the depth is read");

        agent.QueueCount.ShouldBeGreaterThan(0,
            "The slot's listening agent reports no depth while the companion queue holds a backlog, so back " +
            "pressure can never engage for a global partition slot");
    }

    /// <summary>
    /// A dormant inbox row on a durable local queue outside the topology: Incoming, owner 0, exactly what the
    /// durability agent exists to recover.
    /// </summary>
    private async Task<Guid> seedDormantWitnessRowAsync()
    {
        var id = Guid.NewGuid();
        var message = new ShardPathWitness(id);
        var serializer = _runtime.Options.DefaultSerializer!;

        var envelope = new Envelope(message)
        {
            Id = id,
            Destination = new Uri("local://shardpath-witness/"),
            Status = EnvelopeStatus.Incoming,
            OwnerId = TransportConstants.AnyNode,
            ContentType = serializer.ContentType,
            MessageType = typeof(ShardPathWitness).ToMessageTypeName(),
            SentAt = DateTimeOffset.UtcNow
        };

        envelope.Data = serializer.Write(envelope);

        await _store.Inbox.StoreIncomingAsync(envelope);

        return id;
    }

    private static Task waitForAsync(Func<bool> condition, Func<string> diagnostic)
    {
        return waitForAsync(() => Task.FromResult(condition()), () => Task.FromResult(diagnostic()));
    }

    private static Task waitForAsync(Func<bool> condition, Func<Task<string>> diagnostic)
    {
        return waitForAsync(() => Task.FromResult(condition()), diagnostic);
    }

    private static async Task waitForAsync(Func<Task<bool>> condition, Func<Task<string>> diagnostic)
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException(await diagnostic());
    }
}

public record ShardPathMessage(Guid GroupId, int Number, string? Failure);

public class DeadLetterMeException() : Exception("Fail so that the message is dead lettered");

public class RetryMeLaterException() : Exception("Fail so that the message is retried on a schedule");

public class RequeueMeException() : Exception("Fail so that the message is requeued");

public static class ShardPathHandler
{
    public static readonly ConcurrentBag<(Guid GroupId, int Number, Guid EnvelopeId, Uri? Destination, int Attempts, string Diagnostics)>
        Received = new();

    public static readonly ConcurrentBag<int> Handled = new();

    /// <summary>Group ids whose FIRST execution throws the exception named by the message's Failure.</summary>
    public static readonly ConcurrentDictionary<Guid, bool> FailFirstExecution = new();

    /// <summary>Group ids observed executing concurrently -- the one thing global partitioning forbids.</summary>
    public static readonly ConcurrentBag<string> Violations = new();

    private static readonly ConcurrentDictionary<Guid, int> _running = new();

    public static TimeSpan Delay = 150.Milliseconds();

    public static void Reset()
    {
        Received.Clear();
        Handled.Clear();
        FailFirstExecution.Clear();
        Violations.Clear();
        _running.Clear();
        Delay = 150.Milliseconds();
    }

    public static string Describe()
    {
        return Received.Select(x => $"{x.GroupId}/{x.Number}#{x.Attempts}@{x.Destination} [{x.Diagnostics}]").Join(", ");
    }

    public static async Task Handle(ShardPathMessage message, Envelope envelope)
    {
        if (!_running.TryAdd(message.GroupId, message.Number))
        {
            Violations.Add($"{message.GroupId}/{message.Number} started while /{_running[message.GroupId]} was running");
        }

        try
        {
            Received.Add((message.GroupId, message.Number, envelope.Id, envelope.Destination, envelope.Attempts,
                $"listener={envelope.Listener?.Address} persisted={envelope.WasPersistedInInbox} status={envelope.Status} owner={envelope.OwnerId} scheduled={envelope.ScheduledTime}"));

            if (FailFirstExecution.TryRemove(message.GroupId, out _))
            {
                throw message.Failure switch
                {
                    "DeadLetter" => new DeadLetterMeException(),
                    "ScheduleRetry" => new RetryMeLaterException(),
                    "Requeue" => new RequeueMeException(),
                    _ => new InvalidOperationException($"Unknown failure shape '{message.Failure}'")
                };
            }

            await Task.Delay(Delay);
            Handled.Add(message.Number);
        }
        finally
        {
            _running.TryRemove(message.GroupId, out _);
        }
    }
}

/// <summary>Nothing to do with the topology: the durability agent's liveness witness.</summary>
public record ShardPathWitness(Guid Id);

public static class ShardPathWitnessHandler
{
    public static readonly ConcurrentBag<Guid> Received = new();

    public static void Handle(ShardPathWitness message) => Received.Add(message.Id);
}
