using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
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
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4776.
///
/// <para>
/// A message that took <c>GlobalPartitionedRoute</c>'s local shortcut is received on the slot's companion local
/// queue, so that address is the <c>received_at</c> its inbox row carries -- and the dead letter row keeps it
/// (<c>DatabasePersistence</c> writes <c>received_at</c> from <c>envelope.Destination</c>). Replaying that dead
/// letter re-inserts it as <c>Incoming</c> with <c>owner_id = 0</c> at the same address, and the per-database
/// durability agent then recovers it into ITS OWN companion queue and runs it -- on whatever node happens to
/// hold that database's agent, which is not the node that owns the slot. The owner may be working through other
/// messages of the same group id at that moment, so they run concurrently, which is the one thing global
/// partitioning exists to prevent.
/// </para>
///
/// <para>
/// A graceful shutdown produces the identical row: <c>ReleaseAllOwnershipAsync</c> zeroes <c>owner_id</c> and
/// never touches <c>received_at</c>, so the stopping node's companion-queue backlog is left at that address for
/// the agent to mis-claim. Both halves of the report are the same defect, and they are both fixed here, because
/// the fix is about who may recover the address rather than about how the row got there.
/// </para>
///
/// <para>
/// The agent's existing GH-3590 carve-out could not catch this. It skips <em>single node listeners</em>, and the
/// companion queue is deliberately not one: <c>LocalQueue.IsSingleNodeListener</c> is false (GH-3856) and
/// <c>FindListenerCircuit</c> builds a circuit for any <c>local://</c> scheme, so the agent did not merely have
/// the wrong opinion -- it succeeded. Recovery for a companion queue is now pinned to the slot's own exclusive
/// listener, which is the only thing that knows where the slot lives.
/// </para>
///
/// <para>
/// A Solo host owns every slot, which gives both sides of the question honestly: the owner path as it normally
/// runs, and a genuine non-owner produced by stopping one slot's exclusive listener rather than by standing up a
/// second node. Note that in Solo the owner IS the durability agent, so the two end-to-end tests here prove
/// delivery is unbroken; <see cref="a_dormant_companion_row_waits_for_the_slots_owner"/> is the one that proves
/// the defect is gone.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class Bug_4776_recovered_partition_slot_rows_run_on_the_owner : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private IMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        ReplayedSlotHandler.Reset();
        PlainQueueWitnessHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Keep the polling tight so the tests do not wait out the 5 second default. Both knobs matter:
                // ScheduledJobFirstExecution defaults to a RANDOM 500-5000ms, so without pinning it the window
                // in which a_dormant_companion_row_waits_for_the_slots_owner observes the row is sometimes
                // shorter than the agent's very first recovery pass -- which makes that test pass for the wrong
                // reason, intermittently. It has a liveness witness besides, but the cadence should not be a
                // coin flip.
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "slot4776",
                        transportSchema: "slot4776_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ReplayedSlotHandler));

                opts.OnException<InvalidOperationException>().MoveToErrorQueue();

                opts.MessagePartitioning.ByMessage<ReplayedSlotMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues("replayslot", 2);
                    topology.Message<ReplayedSlotMessage>();
                });

                // A durable local queue with no part in the topology at all -- the control for every assertion
                // below about what is special, and the liveness witness for the one negative test.
                opts.LocalQueue("plain-replays").UseDurableInbox();
                opts.Discovery.IncludeType(typeof(PlainQueueWitnessHandler));
                opts.PublishMessage<PlainQueueWitness>().ToLocalQueue("plain-replays");
            }).StartAsync();

        _runtime = _host.GetRuntime();
        _store = _host.Services.GetRequiredService<IMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private PostgresqlQueue[] theSlots()
    {
        var transport = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        return [transport.Queues["replayslot1"], transport.Queues["replayslot2"]];
    }

    /// <summary>
    /// The predicate every durability agent now asks, against the URIs the real topology stamps rather than ones
    /// this test invented. If the reverse lookup ever stopped agreeing with
    /// <c>GlobalPartitionedMessageTopology</c>, the skip would silently stop happening and every behavioural
    /// test that seeds its own address would still pass.
    /// </summary>
    [Fact]
    public void a_companion_queue_recovers_through_its_slot_not_the_durability_agent()
    {
        var slots = theSlots();
        slots.Length.ShouldBe(2);

        foreach (var slot in slots)
        {
            var companion = slot.GlobalPartitionLocalQueueUri
                .ShouldNotBeNull("The topology is supposed to tag every external slot with its companion queue");

            _runtime.Endpoints.ListenerOwnsItsInboxRecovery(companion)
                .ShouldBeTrue($"The companion queue {companion} must be left to the owner of {slot.Uri}");

            // The slot's own address was already covered, by GH-3590 rather than by this change
            _runtime.Endpoints.ListenerOwnsItsInboxRecovery(slot.Uri).ShouldBeTrue();
        }
    }

    /// <summary>
    /// The negative control, and the one that keeps this change from starving ordinary traffic. A durable local
    /// queue outside any global partitioned topology is still the durability agent's to recover -- GH-3856 is a
    /// whole bug about exactly that going unclaimed -- and so is an address whose endpoint no longer exists.
    /// </summary>
    [Fact]
    public void a_plain_durable_local_queue_is_still_the_durability_agents_to_recover()
    {
        _runtime.Endpoints.ListenerOwnsItsInboxRecovery(new Uri("local://plain-replays/")).ShouldBeFalse();
        _runtime.Endpoints.ListenerOwnsItsInboxRecovery(new Uri("local://never-configured/")).ShouldBeFalse();
    }

    /// <summary>
    /// The reported defect. With the slot's listener stopped this node is a genuine non-owner, exactly as the
    /// node holding the durability agent is in the report, and a row released or replayed at the companion
    /// address must not be claimed here. Restarting the listener then delivers it, which is what makes this a
    /// deferral rather than the kind of stranding GH-3856 was about.
    ///
    /// <para>
    /// The witness row is what makes the negative assertion mean anything. "The companion row did not run" is
    /// also true of a sweep that never happened, and the durability agent's first pass is late by design
    /// (<c>ScheduledJobFirstExecution</c>), so this test passed with the fix reverted until a dormant row on an
    /// ordinary durable local queue was seeded alongside it. Waiting for THAT one to be recovered proves the
    /// agent was awake and claiming rows during the very window in which it left the companion row alone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task a_dormant_companion_row_waits_for_the_slots_owner()
    {
        var slot = theSlots()[0];
        var companion = slot.GlobalPartitionLocalQueueUri!;

        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);

        _runtime.Endpoints.FindListeningAgent(slot.Uri)?.Status
            .ShouldNotBe(ListeningStatus.Accepting, "Precondition: this node must NOT own the slot");

        // Exactly the row MoveReplayableErrorMessagesToIncomingOperation writes, and exactly the row
        // ReleaseAllOwnershipAsync leaves behind: Incoming, owner 0, still addressed to the companion queue.
        var id = await seedDormantCompanionRowAsync(companion);
        var witness = await seedDormantWitnessRowAsync();

        await waitForAsync(() => PlainQueueWitnessHandler.Received.Contains(witness),
            () => "The durability agent never recovered the witness row, so this test could not have " +
                  "observed it declining to recover the companion row either");

        ReplayedSlotHandler.Received.ShouldNotContain(x => x.Id == id,
            "A node that does not own the slot must not execute the slot's messages");

        (await _store.LoadPageOfGloballyOwnedIncomingAsync(companion, 10))
            .ShouldContain(x => x.Id == id, "The row has to still be dormant, not consumed and not deleted");

        // Deferred, not stranded: give the slot its listener back and the owner picks it up.
        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        await waitForAsync(() => ReplayedSlotHandler.Received.Any(x => x.Id == id),
            () => $"received=[{ReplayedSlotHandler.Describe()}]");

        ReplayedSlotHandler.Received.ShouldContain(x => x.Id == id && x.Destination == companion);
    }

    /// <summary>
    /// The owner path. This host listens to every slot, so a dormant row at a companion address is its to run,
    /// and the loop that claims it is the one <c>ListeningAgent</c> starts alongside the slot. Without that
    /// half, skipping the agent would simply trade a message running in the wrong place for a message that
    /// never runs at all.
    /// </summary>
    [Fact]
    public async Task the_slot_owner_recovers_a_dormant_companion_row_itself()
    {
        var slot = theSlots()[1];
        var companion = slot.GlobalPartitionLocalQueueUri!;

        _runtime.Endpoints.FindListeningAgent(slot.Uri)!.Status.ShouldBe(ListeningStatus.Accepting);

        var id = await seedDormantCompanionRowAsync(companion);

        await waitForAsync(() => ReplayedSlotHandler.Received.Any(x => x.Id == id),
            () => $"received=[{ReplayedSlotHandler.Describe()}]");

        ReplayedSlotHandler.Received.ShouldContain(x => x.Id == id && x.Destination == companion);
    }

    /// <summary>
    /// The reported flow end to end, through the real dead letter tables rather than a hand-seeded row: publish,
    /// fail, dead letter, mark replayable, and let the durability agent's
    /// <c>MoveReplayableErrorMessagesToIncomingOperation</c> write the <c>Incoming</c> row itself. The point is
    /// that the message still comes back, and comes back through the companion queue so it is sequenced with the
    /// rest of its group id.
    /// </summary>
    [Fact]
    public async Task a_replayed_dead_letter_comes_back_through_the_companion_queue()
    {
        var id = Guid.NewGuid();
        ReplayedSlotHandler.FailOnce.TryAdd(id, true);

        await _host.MessageBus().PublishAsync(new ReplayedSlotMessage(id));

        await waitForAsync(() => ReplayedSlotHandler.Received.Any(x => x.Id == id),
            () => $"The first attempt never ran. received=[{ReplayedSlotHandler.Describe()}]");

        // Precondition for the whole issue: the first attempt took the local shortcut, so the dead letter row
        // -- and therefore the replayed inbox row -- is addressed to a companion queue rather than to the slot.
        var first = ReplayedSlotHandler.Received.First(x => x.Id == id);
        _runtime.Endpoints.GlobalPartitionSlotFor(first.Destination!)
            .ShouldNotBeNull($"Expected the first attempt to run on a companion queue, but it ran on {first.Destination}");

        // Marked on a loop because the dead letter row may not be written yet on the first pass, and marking
        // nothing is harmless. Scoped to this envelope id: AutoPurgeOnStartup does not clear the dead letter
        // table, so a blanket "replay everything" would drag in every other test's failures.
        await waitForAsync(async () =>
        {
            await _store.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync([first.EnvelopeId]);
            return ReplayedSlotHandler.Received.Count(x => x.Id == id) >= 2;
        }, () => $"The replayed dead letter never came back. envelope={first.EnvelopeId} " +
                 $"received=[{ReplayedSlotHandler.Describe()}]");

        ReplayedSlotHandler.Received.Where(x => x.Id == id)
            .ShouldAllBe(x => x.Destination == first.Destination);
    }

    /// <summary>
    /// The liveness witness: the same dormant row shape, on a durable local queue that has nothing to do with
    /// the topology, so the durability agent is still its rightful recoverer.
    /// </summary>
    private async Task<Guid> seedDormantWitnessRowAsync()
    {
        var id = Guid.NewGuid();
        await seedDormantRowAsync(new PlainQueueWitness(id), id, new Uri("local://plain-replays/"));
        return id;
    }

    private async Task<Guid> seedDormantCompanionRowAsync(Uri companion)
    {
        var id = Guid.NewGuid();
        await seedDormantRowAsync(new ReplayedSlotMessage(id), id, companion);
        return id;
    }

    private async Task seedDormantRowAsync(object message, Guid id, Uri destination)
    {
        var serializer = _runtime.Options.DefaultSerializer!;

        var envelope = new Envelope(message)
        {
            Id = id,
            Destination = destination,
            Status = EnvelopeStatus.Incoming,
            OwnerId = TransportConstants.AnyNode,
            ContentType = serializer.ContentType,
            MessageType = message.GetType().ToMessageTypeName(),
            SentAt = DateTimeOffset.UtcNow
        };

        envelope.Data = serializer.Write(envelope);

        await _store.Inbox.StoreIncomingAsync(envelope);
    }

    private static Task waitForAsync(Func<bool> condition, Func<string> diagnostic)
    {
        return waitForAsync(() => Task.FromResult(condition()), diagnostic);
    }

    private static async Task waitForAsync(Func<Task<bool>> condition, Func<string> diagnostic)
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException(diagnostic());
    }
}

public record ReplayedSlotMessage(Guid Id);

public static class ReplayedSlotHandler
{
    public static readonly ConcurrentBag<(Guid Id, Guid EnvelopeId, Uri? Destination)> Received = new();

    /// <summary>Ids whose FIRST execution throws, so the message is dead lettered and can then be replayed.</summary>
    public static readonly ConcurrentDictionary<Guid, bool> FailOnce = new();

    public static void Reset()
    {
        Received.Clear();
        FailOnce.Clear();
    }

    public static string Describe()
    {
        return Received.Select(x => $"{x.Id}@{x.Destination}").Join(", ");
    }

    public static void Handle(ReplayedSlotMessage message, Envelope envelope)
    {
        // The ENVELOPE id, not the message id: that is what the dead letter table is keyed on, and therefore
        // what a replay has to be asked for.
        Received.Add((message.Id, envelope.Id, envelope.Destination));

        if (FailOnce.TryRemove(message.Id, out _))
        {
            throw new InvalidOperationException("Fail so that the message is dead lettered");
        }
    }
}

/// <summary>
/// Nothing to do with global partitioning -- deliberately a separate message type so no grouping rule, slot
/// routing or re-route interceptor can touch it. See
/// <c>Bug_4776_recovered_partition_slot_rows_run_on_the_owner.a_dormant_companion_row_waits_for_the_slots_owner</c>.
/// </summary>
public record PlainQueueWitness(Guid Id);

public static class PlainQueueWitnessHandler
{
    public static readonly ConcurrentBag<Guid> Received = new();

    public static void Handle(PlainQueueWitness message)
    {
        Received.Add(message.Id);
    }
}
