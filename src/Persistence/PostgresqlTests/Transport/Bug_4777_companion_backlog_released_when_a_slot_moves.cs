using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace PostgresqlTests.Transport;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4777.
///
/// <para>
/// When a global partition slot moves, the losing node kept working through the backlog already sitting in the
/// slot's companion local queue while the gaining node started on new messages of the same group ids. The
/// leader is not at fault: <c>ReassignAgent</c> awaits a CONFIRMED stop on the losing node before it cascades
/// the <c>AssignAgent</c> to the gaining one, and says so -- "Only a CONFIRMED stop may be followed by a start
/// elsewhere". The problem is what that confirmed stop was worth.
/// </para>
///
/// <para>
/// <c>StopAndDrainCoreAsync</c>'s two protective steps are <c>LatchReceiver()</c> and
/// <c>receiver.DrainAsync()</c>, and for a slot both were no-ops. The receiver is a
/// <c>GlobalPartitionedReceiverBridge</c>, which is not an <c>IReceiverWrapper</c> -- so <c>Unwrap()</c> stops
/// at the bridge and finds no <c>ILatchedReceiver</c> -- and whose <c>DrainAsync</c> returned
/// <c>ValueTask.CompletedTask</c> under the comment "the local queue handles its own draining". True of the
/// queue; false of the handoff, because nothing called it. So the agent reported a clean stop while the
/// companion queue carried on executing.
/// </para>
///
/// <para>
/// GH-4188 taught <c>Unwrap()</c> about <c>ReceiverWithRules</c> and <c>GlobalPartitionedInterceptor</c>; the
/// bridge was never made a wrapper, which left the slot path in the pre-GH-3709 state that
/// <c>LatchReceiver</c>'s own comment describes as fixed.
/// </para>
///
/// <para>
/// A Solo host gives a genuine, deterministic handoff: stopping one slot's exclusive listener is exactly what
/// the losing node does, without the timing lottery of standing up a second node. The released rows then need
/// GH-4776's companion recovery loop to reach whoever owns the slot next, which is why that had to land first.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class Bug_4777_companion_backlog_released_when_a_slot_moves : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private IMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        SlowSlotHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "slot4777",
                        transportSchema: "slot4777_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SlowSlotHandler));

                opts.MessagePartitioning.ByMessage<SlowSlotMessage>(x => x.GroupId.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues("movingslot", 2);
                    topology.Message<SlowSlotMessage>();
                });
            }).StartAsync();

        _runtime = _host.GetRuntime();
        _store = _host.Services.GetRequiredService<IMessageStore>();

        // Load-bearing, and not the same thing as AutoPurgeOnStartup -- that is configured on the TRANSPORT and
        // purges the queue tables, leaving wolverine_incoming_envelopes untouched. Leftovers from an earlier run
        // do not merely skew the counts here: GH-4776's companion recovery loop claims any dormant row at this
        // address the moment the slot starts, so a previous run's backlog gets executed into
        // SlowSlotHandler.Handled and every count in this fixture is wrong.
        //
        // Deliberately a targeted delete rather than IMessageStoreAdmin.ClearAllAsync(), which also deletes the
        // node record, listener and agent-restriction tables. Doing that to a host that is already RUNNING pulls
        // the agent bookkeeping out from under it, and the symptom is not a clean failure -- the slot listener
        // simply stops making progress and the fixture hangs until its timeout.
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        var delete = conn.CreateCommand();
        delete.CommandText = "delete from slot4777.wolverine_incoming_envelopes";
        await delete.ExecuteNonQueryAsync();
        await conn.CloseAsync();

        // Cleared AFTER the delete, so anything a leftover row managed to run before it is not counted.
        SlowSlotHandler.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        SlowSlotHandler.Reset();
        await _host.StopAsync();
        _host.Dispose();
    }

    private PostgresqlQueue theSlot() =>
        _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["movingslot1"];

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
            var destination = bus.PreviewSubscriptions(new SlowSlotMessage(candidate, 0)).Single().Destination;
            if (destination == companion) return candidate;
        }

        throw new TimeoutException($"Could not find a group id routing to {companion}");
    }

    /// <summary>
    /// The reported defect. Execution has to stop at the moment the slot's stop returns -- that return is what
    /// the leader waits for before starting the slot elsewhere, so anything this node runs afterwards runs
    /// beside the new owner under the same group id.
    /// </summary>
    [Fact]
    public async Task losing_the_slot_stops_execution_and_releases_the_rest_of_the_backlog()
    {
        var slot = theSlot();
        var companion = slot.GlobalPartitionLocalQueueUri!;
        var groupId = groupIdLandingOn(companion);

        var bus = _host.MessageBus();
        const int total = 14;
        for (var i = 1; i <= total; i++)
        {
            await bus.PublishAsync(new SlowSlotMessage(groupId, i));
        }

        // Let a couple through so there is genuinely a backlog left to strand, without waiting so long that
        // the queue empties on its own.
        await waitForAsync(() => SlowSlotHandler.Handled.Count >= 2,
            () => $"The backlog never started. handled={SlowSlotHandler.Handled.Count}");

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        // The count at the instant the stop returned. Nothing may be added to it after this point.
        var atStop = SlowSlotHandler.Handled.Count;

        atStop.ShouldBeLessThan(total,
            "Precondition: the queue has to still have a backlog when the slot is given up, or this test " +
            "proves nothing. Lower the handler delay or raise the message count if this trips.");

        // Several handler durations and several durability cycles. Before this fix the companion queue kept
        // draining straight through here.
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        SlowSlotHandler.Handled.Count.ShouldBe(atStop,
            $"A node that has given up the slot must not execute any more of its messages. Handled {atStop} " +
            $"at the stop and {SlowSlotHandler.Handled.Count} two seconds later");

        // "Finish or release" -- the remainder is back at owner 0 for whoever owns the slot next, not lost and
        // not still owned by this node.
        var dormant = await _store.LoadPageOfGloballyOwnedIncomingAsync(companion, total * 2);

        dormant.Count.ShouldBeGreaterThan(0, "The un-executed backlog has to be released, not dropped");

        // Deliberately >= rather than ==. DurableReceiver.DrainAsync releases owner_id for the whole address in
        // one statement, so a message that finished DURING the drain can be both marked handled and released --
        // and then run once more on the next owner. That is at-least-once delivery doing what it promises at a
        // drain boundary, not a defect, and asserting exactly-once here would be asserting a guarantee Wolverine
        // does not make. What must hold is that nothing is LOST.
        (atStop + dormant.Count).ShouldBeGreaterThanOrEqualTo(total,
            "Every message should be either executed or released at the companion address");
    }

    /// <summary>
    /// The other half, and the reason the drain is allowed to be destructive: re-acquiring the slot rebuilds the
    /// companion queue's receiver (<c>DurableReceiver</c> has <c>Latch()</c> and no unlatch, so a drained queue
    /// stays dead) and GH-4776's recovery loop then picks the released rows back up. Without the rebuild this
    /// would be a silent black hole rather than a handoff.
    /// </summary>
    [Fact]
    public async Task re_acquiring_the_slot_rebuilds_the_queue_and_finishes_the_backlog()
    {
        var slot = theSlot();
        var companion = slot.GlobalPartitionLocalQueueUri!;
        var groupId = groupIdLandingOn(companion);

        var bus = _host.MessageBus();
        const int total = 14;
        for (var i = 1; i <= total; i++)
        {
            await bus.PublishAsync(new SlowSlotMessage(groupId, i));
        }

        await waitForAsync(() => SlowSlotHandler.Handled.Count >= 2,
            () => $"The backlog never started. handled={SlowSlotHandler.Handled.Count}");

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);
        SlowSlotHandler.Handled.Count.ShouldBeLessThan(total, "Precondition: a backlog has to be left over");

        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        // Completeness, not uniqueness: every message has to arrive, and a duplicate at the drain boundary is
        // permitted (see the note in losing_the_slot_...). Asserting on the SET is what distinguishes "the
        // backlog was finished" from "the count happened to reach 14 by running two of them twice".
        var expected = Enumerable.Range(1, total).ToArray();
        await waitForAsync(() => expected.All(SlowSlotHandler.Handled.Contains),
            () => $"The released backlog was never finished after the slot came back. missing=" +
                  $"[{string.Join(", ", expected.Where(n => !SlowSlotHandler.Handled.Contains(n)))}]");

        (await _store.LoadPageOfGloballyOwnedIncomingAsync(companion, total * 2)).ShouldBeEmpty();
    }

    /// <summary>
    /// The negative control. A rate-limit or circuit-breaker pause is NOT a handoff: this node still owns the
    /// slot, nothing else will touch those group ids, so the backlog must be left alone rather than dumped into
    /// the inbox. Only a real stop drains the companion queue.
    /// </summary>
    [Fact]
    public async Task a_back_pressure_latch_does_not_release_the_backlog()
    {
        var slot = theSlot();
        var companion = slot.GlobalPartitionLocalQueueUri!;
        var groupId = groupIdLandingOn(companion);

        var bus = _host.MessageBus();
        const int total = 10;
        for (var i = 1; i <= total; i++)
        {
            await bus.PublishAsync(new SlowSlotMessage(groupId, i));
        }

        await waitForAsync(() => SlowSlotHandler.Handled.Count >= 1,
            () => "The backlog never started");

        var agent = _runtime.Endpoints.FindListeningAgent(slot.Uri)!;
        await agent.MarkAsTooBusyAndStopReceivingAsync();

        // The companion queue keeps going, because this node has not given up the slot
        await waitForAsync(() => SlowSlotHandler.Handled.Count == total,
            () => $"A back-pressure latch on the slot must not stop the companion queue. " +
                  $"handled={SlowSlotHandler.Handled.Count}/{total}");
    }

    private static async Task waitForAsync(Func<bool> condition, Func<string> diagnostic)
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50.Milliseconds());
        }

        throw new TimeoutException(diagnostic());
    }
}

public record SlowSlotMessage(Guid GroupId, int Number);

public static class SlowSlotHandler
{
    public static readonly ConcurrentBag<int> Handled = new();

    public static void Reset() => Handled.Clear();

    /// <summary>
    /// Slow enough that a backlog reliably exists when the slot is given up, fast enough that the whole fixture
    /// stays well inside its timeouts. The companion queue is sequential per group id, so 14 messages is roughly
    /// two seconds of work.
    /// </summary>
    public static async Task Handle(SlowSlotMessage message)
    {
        await Task.Delay(150.Milliseconds());
        Handled.Add(message.Number);
    }
}
