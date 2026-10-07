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
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4866, reported by @alexandrefresnais.
///
/// <para>
/// GH-4777 made losing a global partition slot latch and drain the slot's companion queue before the agent
/// reports a clean stop. That handoff never reached a slot whose listener was paused by back pressure.
/// <c>MarkAsTooBusyAndStopReceivingAsync</c> stops and disposes the <c>IListener</c>, sets <c>Listener</c> to
/// null and <c>Status</c> to <c>TooBusy</c>, and deliberately keeps the receiver alive -- the backlog it holds
/// is exactly what has to drain before the listener may resume. <c>StopAndDrainCoreAsync</c> then returned on
/// <c>listener == null</c>, before the latch, the receiver drain, the companion drain and the flip to
/// <c>Stopped</c>. So <c>ExclusiveListenerAgent.StopAsync</c> still reported <c>Stopped</c>, the leader started
/// the slot on the gaining node, and the losing node carried on executing its backlog beside it.
/// </para>
///
/// <para>
/// Then the second half: <c>Status</c> was still <c>TooBusy</c>, and the <c>BackPressureAgent</c> is only
/// disposed with the listening agent itself. Once the backlog fell under <c>BufferingLimits.Restart</c> the
/// next sweep called <c>StartAsync()</c>, and the node polled the slot's queue table again, beside the new
/// owner, with the slot assigned to somebody else. In the field this was 50 intra-group overlaps per run.
/// </para>
///
/// <para>
/// A Solo host gives the deterministic shape, as in the GH-4777 fixture: pausing the slot by back pressure
/// is exactly what the sweeper does, and stopping the slot's listener is exactly what the losing node's
/// exclusive agent does. The sweeper itself is real here (every 2 seconds), which is what the revival test
/// relies on.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class Bug_4866_slot_lost_while_latched_by_back_pressure : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private IMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        BusySlotHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "slot4866",
                        transportSchema: "slot4866_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(BusySlotHandler));

                opts.MessagePartitioning.ByMessage<BusySlotMessage>(x => x.GroupId.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    // The reporter's limits: small enough that a modest backlog trips back pressure
                    topology.UseShardedPostgresqlQueues("busyslot", 2,
                        t => t.ConfigureListening(x => x.UseDurableInbox(new BufferingLimits(5, 2))));
                    topology.Message<BusySlotMessage>();
                });
            }).StartAsync();

        _runtime = _host.GetRuntime();
        _store = _host.Services.GetRequiredService<IMessageStore>();

        // Same reasoning as the GH-4777 fixture: AutoPurgeOnStartup clears the queue tables, not the inbox, and
        // GH-4776's companion recovery loop would execute a previous run's leftovers into this run's counts.
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        var delete = conn.CreateCommand();
        delete.CommandText = "delete from slot4866.wolverine_incoming_envelopes";
        await delete.ExecuteNonQueryAsync();
        await conn.CloseAsync();

        BusySlotHandler.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        BusySlotHandler.Reset();
        await _host.StopAsync();
        _host.Dispose();
    }

    private PostgresqlQueue theSlot() =>
        _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["busyslot1"];

    private Guid groupIdLandingOn(Uri companion)
    {
        var bus = _host.MessageBus();

        for (var i = 0; i < 500; i++)
        {
            var candidate = Guid.NewGuid();
            var destination = bus.PreviewSubscriptions(new BusySlotMessage(candidate, 0)).Single().Destination;
            if (destination == companion) return candidate;
        }

        throw new TimeoutException($"Could not find a group id routing to {companion}");
    }

    /// <summary>
    /// Publishes a backlog onto the slot's companion queue and pauses the slot by back pressure, which is the
    /// state the leader's stop finds the losing node in. Answers the group id.
    /// </summary>
    private async Task<Guid> backlogOnATooBusySlotAsync(PostgresqlQueue slot, int total)
    {
        var companion = slot.GlobalPartitionLocalQueueUri!;
        var groupId = groupIdLandingOn(companion);

        var bus = _host.MessageBus();
        for (var i = 1; i <= total; i++)
        {
            await bus.PublishAsync(new BusySlotMessage(groupId, i));
        }

        await waitForAsync(() => BusySlotHandler.Handled.Count >= 2,
            () => $"The backlog never started. handled={BusySlotHandler.Handled.Count}");

        // Exactly what BackPressureAgent calls when QueueCount passes BufferingLimits.Maximum. Called
        // directly so the fixture does not depend on winning a race against the 2 second sweep.
        var agent = _runtime.Endpoints.FindListeningAgent(slot.Uri)!;
        await agent.MarkAsTooBusyAndStopReceivingAsync();
        agent.Status.ShouldBe(ListeningStatus.TooBusy, "Precondition: the slot has to be paused by back pressure");

        return groupId;
    }

    /// <summary>
    /// The reported defect, first half: the stop has to mean the same thing for a too-busy slot as for an
    /// accepting one. Execution ends when the stop returns, the status says so, and the remainder is released.
    /// </summary>
    [Fact]
    public async Task losing_a_slot_latched_by_back_pressure_stops_execution_and_releases_the_backlog()
    {
        var slot = theSlot();
        var companion = slot.GlobalPartitionLocalQueueUri!;
        const int total = 20;
        await backlogOnATooBusySlotAsync(slot, total);

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        var atStop = BusySlotHandler.Handled.Count;
        atStop.ShouldBeLessThan(total,
            "Precondition: the queue has to still have a backlog when the slot is given up, or this test " +
            "proves nothing. Lower the handler delay or raise the message count if this trips.");

        var agent = _runtime.Endpoints.FindListeningAgent(slot.Uri)!;
        agent.Status.ShouldBe(ListeningStatus.Stopped,
            "A slot that has been given up is Stopped, whatever state back pressure had left it in");

        // Several handler durations. Before the fix the companion queue kept draining straight through here.
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        BusySlotHandler.Handled.Count.ShouldBe(atStop,
            $"A node that has given up the slot must not execute any more of its messages. Handled {atStop} " +
            $"at the stop and {BusySlotHandler.Handled.Count} two seconds later");

        var dormant = await _store.LoadPageOfGloballyOwnedIncomingAsync(companion, total * 2);
        dormant.Count.ShouldBeGreaterThan(0, "The un-executed backlog has to be released, not dropped");

        // >= rather than ==, for the same at-least-once drain-boundary reason the GH-4777 fixture gives
        (atStop + dormant.Count).ShouldBeGreaterThanOrEqualTo(total,
            "Every message should be either executed or released at the companion address");
    }

    /// <summary>
    /// The second half, and the one that put two owners on the slot for good: once the backlog is under
    /// <c>BufferingLimits.Restart</c>, the back pressure sweep must not revive a listener whose slot has been
    /// given up. Before the fix the status was still <c>TooBusy</c>, so the sweep saw a paused listener with
    /// room again and called <c>StartAsync()</c>.
    /// </summary>
    [Fact]
    public async Task the_back_pressure_sweep_does_not_revive_a_slot_that_was_given_up()
    {
        var slot = theSlot();
        await backlogOnATooBusySlotAsync(slot, 20);

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        var agent = _runtime.Endpoints.FindListeningAgent(slot.Uri)!;

        // Three sweeps at the 2 second interval. Before the fix the companion queue finished the backlog in
        // about three seconds, the queue count fell under Restart, and the next sweep restarted the listener.
        await Task.Delay(7.Seconds(), TestContext.Current.CancellationToken);

        agent.Status.ShouldBe(ListeningStatus.Stopped,
            "Back pressure lifting must not restart a slot this node no longer owns");
        ((ListeningAgent)agent).Listener.ShouldBeNull();
    }

    /// <summary>
    /// The guard that the fix does not over-correct: a slot given up from the too-busy state can still be
    /// re-acquired, and when it is, the released backlog gets finished.
    /// </summary>
    [Fact]
    public async Task re_acquiring_a_slot_given_up_while_too_busy_finishes_the_backlog()
    {
        var slot = theSlot();
        var companion = slot.GlobalPartitionLocalQueueUri!;
        const int total = 20;
        await backlogOnATooBusySlotAsync(slot, total);

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);
        BusySlotHandler.Handled.Count.ShouldBeLessThan(total, "Precondition: a backlog has to be left over");

        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        var expected = Enumerable.Range(1, total).ToArray();
        await waitForAsync(() => expected.All(BusySlotHandler.Handled.Contains),
            () => "The released backlog was never finished after the slot came back. missing=" +
                  $"[{string.Join(", ", expected.Where(n => !BusySlotHandler.Handled.Contains(n)))}]");

        (await _store.LoadPageOfGloballyOwnedIncomingAsync(companion, total * 2)).ShouldBeEmpty();
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

public record BusySlotMessage(Guid GroupId, int Number);

public static class BusySlotHandler
{
    public static readonly ConcurrentBag<int> Handled = new();

    public static void Reset() => Handled.Clear();

    public static async Task Handle(BusySlotMessage message)
    {
        await Task.Delay(150.Milliseconds());
        Handled.Add(message.Number);
    }
}
