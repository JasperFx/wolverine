using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;

namespace PostgresqlTests.Transport;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4822.
///
/// <para>
/// A scheduled message to a global partition parks in the inbox at the EXTERNAL slot's address (GH-4673), so
/// slot ownership is settled when it comes due. A node that never owned the slot has no listening agent for
/// that address and forwards the envelope to the slot. A node that USED to own it still has one -- stopped
/// when the leader moved the slot away, but still registered -- so the scheduled poller handed the envelopes
/// to a listener with no receiver, which threw after the rows had already been committed as Incoming and
/// owned by this live node. Nothing ever recovered them, and every other destination in the same batch was
/// stranded with them.
/// </para>
///
/// <para>
/// A Solo host owns every slot, so stopping one slot's exclusive listener produces a genuine ex-owner without
/// standing up a second node -- the same technique as GH-4700 and GH-4776. The rows are written straight into
/// the inbox and promoted by the host's own scheduled poller, which is the path the report goes through.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class Bug_4822_scheduled_promotion_after_slot_handoff : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;

    public async ValueTask InitializeAsync()
    {
        HandoffStepHandler.Received.Clear();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "slot4822",
                        transportSchema: "slot4822_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(HandoffStepHandler));

                opts.MessagePartitioning.ByMessage<HandoffStep>(x => x.GroupId.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues("handoff", 2);
                    topology.Message<HandoffStep>();
                });

                // An ordinary exclusive listener with no partitioning behind it. Stopped, it still has no
                // receiver to take promoted envelopes, which makes it a dependable way to fail one group
                // of a scheduled batch on purpose.
                opts.ListenToPostgresqlQueue("handoffplain").ListenWithStrictOrdering();
            }).StartAsync();

        _runtime = _host.GetRuntime();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private PostgresqlQueue[] theSlots()
    {
        var transport = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        return [transport.Queues["handoff1"], transport.Queues["handoff2"]];
    }

    private PostgresqlQueue thePlainQueue()
    {
        return _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["handoffplain"];
    }

    /// <summary>
    /// Shaped like the row a cascaded <c>ScheduledAt</c> leaves behind: Scheduled, owned by any node, parked
    /// at its eventual destination and already due. <paramref name="dueAgo"/> orders the rows inside the
    /// poller's batch, which is sorted by execution time.
    /// </summary>
    private async Task<(Guid GroupId, Guid EnvelopeId)> parkScheduledAsync(Uri destination, TimeSpan dueAgo)
    {
        var message = new HandoffStep(Guid.NewGuid());
        var serializer = _runtime.Options.DefaultSerializer;

        var envelope = new Envelope(message)
        {
            Destination = destination,
            MessageType = typeof(HandoffStep).ToMessageTypeName(),
            Serializer = serializer,
            ContentType = serializer.ContentType,
            Status = EnvelopeStatus.Scheduled,
            OwnerId = TransportConstants.AnyNode,
            ScheduledTime = DateTimeOffset.UtcNow.Subtract(dueAgo)
        };

        envelope.Data = serializer.Write(envelope);

        await _runtime.Storage.Inbox.StoreIncomingAsync(envelope);

        return (message.GroupId, envelope.Id);
    }

    private async Task<Envelope?> inboxRowAsync(Guid id)
    {
        var all = await _runtime.Storage.Admin.AllIncomingAsync();
        return all.FirstOrDefault(x => x.Id == id);
    }

    private string describeInbox(IEnumerable<Envelope> rows)
    {
        return rows.Select(x => $"{x.Id}@{x.Destination} {x.Status} owner={x.OwnerId}").Join("; ");
    }

    /// <summary>
    /// Against the URIs the topology itself stamps, for the reason GH-4700's twin gives: a lookup that only
    /// agrees with hand-built endpoints would let the fix silently do nothing.
    /// </summary>
    [Fact]
    public void only_the_external_slots_themselves_are_global_partition_slots()
    {
        foreach (var slot in theSlots())
        {
            _runtime.Endpoints.IsGlobalPartitionSlot(slot.Uri).ShouldBeTrue();

            // The companion queue is GlobalPartitionSlotFor's question, not this one
            _runtime.Endpoints.IsGlobalPartitionSlot(slot.GlobalPartitionLocalQueueUri!).ShouldBeFalse();
        }

        _runtime.Endpoints.IsGlobalPartitionSlot(thePlainQueue().Uri).ShouldBeFalse();
        _runtime.Endpoints.IsGlobalPartitionSlot(new Uri("postgresql://nowhere/")).ShouldBeFalse();
    }

    /// <summary>
    /// The reported defect. The slot's row comes first in the batch so that, before the fix, the throw it
    /// caused also stranded the other slot's row -- the "7 rows for repro1" half of the report.
    /// </summary>
    [Fact]
    public async Task an_ex_owner_forwards_a_due_message_to_the_slot_and_the_rest_of_the_batch_still_runs()
    {
        var (lost, kept) = (theSlots()[0], theSlots()[1]);

        await _runtime.Endpoints.StopListenerAsync(lost, TestContext.Current.CancellationToken);

        _runtime.Endpoints.FindListeningAgent(lost.Uri).ShouldNotBeNull(
            "Precondition: an ex-owner still has the stopped listening agent registered");

        var forLostSlot = await parkScheduledAsync(lost.Uri, 2.Seconds());
        var forKeptSlot = await parkScheduledAsync(kept.Uri, 1.Seconds());

        // The slot this node kept is unaffected by the one it gave up
        await waitForAsync(() => HandoffStepHandler.Received.Any(x => x.Id == forKeptSlot.GroupId),
            async () => describeInbox(await _runtime.Storage.Admin.AllIncomingAsync()));

        // The given-up slot's message left the inbox here without running here ...
        await waitForAsync(async () => await inboxRowAsync(forLostSlot.EnvelopeId) == null,
            async () => describeInbox(await _runtime.Storage.Admin.AllIncomingAsync()));

        HandoffStepHandler.Received.ShouldNotContain(x => x.Id == forLostSlot.GroupId);

        // ... and was handed to the slot rather than dropped: the slot's owner -- this node again, once it
        // gets the listener back -- runs it, exactly once.
        await _runtime.Endpoints.StartListenerAsync(lost, TestContext.Current.CancellationToken);

        await waitForAsync(() => HandoffStepHandler.Received.Any(x => x.Id == forLostSlot.GroupId),
            async () => describeInbox(await _runtime.Storage.Admin.AllIncomingAsync()));

        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        HandoffStepHandler.Received.Count(x => x.Id == forLostSlot.GroupId).ShouldBe(1);
        HandoffStepHandler.Received.Single(x => x.Id == forLostSlot.GroupId).Destination.ShouldBe(lost.Uri);
    }

    /// <summary>
    /// The general hardening. Whatever makes one destination of a promoted batch fail, the rest of the batch
    /// still runs, and the failed rows are handed back to any node instead of staying owned by this live one
    /// where no recovery would ever look at them.
    /// </summary>
    [Fact]
    public async Task a_destination_that_cannot_take_promoted_envelopes_does_not_strand_its_rows_or_the_batch()
    {
        var plain = thePlainQueue();
        var kept = theSlots()[1];

        await _runtime.Endpoints.StopListenerAsync(plain, TestContext.Current.CancellationToken);

        var failing = await parkScheduledAsync(plain.Uri, 2.Seconds());
        var healthy = await parkScheduledAsync(kept.Uri, 1.Seconds());

        await waitForAsync(() => HandoffStepHandler.Received.Any(x => x.Id == healthy.GroupId),
            async () => describeInbox(await _runtime.Storage.Admin.AllIncomingAsync()));

        // Promoted, then released rather than stranded under this node's id
        await waitForAsync(async () => await inboxRowAsync(failing.EnvelopeId) is { Status: EnvelopeStatus.Incoming } row
                                       && row.OwnerId == TransportConstants.AnyNode,
            async () => describeInbox(await _runtime.Storage.Admin.AllIncomingAsync()));

        HandoffStepHandler.Received.ShouldNotContain(x => x.Id == failing.GroupId);
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

        throw new TimeoutException($"Condition never met. Inbox: {await diagnostic()}");
    }
}

public record HandoffStep(Guid GroupId);

public static class HandoffStepHandler
{
    public static readonly ConcurrentBag<(Guid Id, Uri? Destination)> Received = new();

    public static void Handle(HandoffStep message, Envelope envelope) =>
        Received.Add((message.GroupId, envelope.Destination));
}
