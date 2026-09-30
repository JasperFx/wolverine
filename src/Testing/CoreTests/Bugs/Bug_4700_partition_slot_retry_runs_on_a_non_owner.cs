using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4700.
///
/// <para>
/// A message routed through <c>GlobalPartitionedRoute</c>'s local shortcut is received on the slot's
/// companion local queue, so that is the address its inbox row carries. If the handler fails under a
/// <c>ScheduleRetry</c> policy, the row is parked as <c>Scheduled</c> at that same address and released to
/// <c>AnyNode</c>. When it comes due, whichever node wins the per-database advisory lock promotes it --
/// the scheduled poller filters on nothing but status and execution time -- and hands it to
/// <see cref="WolverineRuntime.EnqueueDirectlyAsync"/>, which finds a listener circuit for the address and
/// runs it. That circuit exists on EVERY node: <c>LocalQueue.IsSingleNodeListener</c> is deliberately false
/// (GH-3856) and <c>FindListenerCircuit</c> builds one for any <c>local://</c> scheme. So the retry executes
/// on a node that does not own the slot, concurrently with the owner and under the same group id, which is
/// the one thing global partitioning exists to prevent.
/// </para>
///
/// <para>
/// GH-4673 fixed the send-time half by refusing the local shortcut for a message that is already scheduled.
/// It cannot help here: the first attempt was an immediate send and the shortcut was correct for it. The
/// retry turns an already-received message into a scheduled one, and by then the row exists.
/// </para>
///
/// <para>
/// Ownership is asked of the EXTERNAL slot, because the companion queue address cannot answer it -- the same
/// <c>FindListeningAgent(...).Status == Accepting</c> question <c>GlobalPartitionedRoute</c> asks at send
/// time, moved to when the message is actually due.
/// </para>
/// </summary>
public class Bug_4700_partition_slot_retry_runs_on_a_non_owner : IAsyncLifetime
{
    // The slot's external endpoint: sendable here, listened to on the node that owns the slot.
    private static readonly Uri TheSlot = "stub://partition-slot-1".ToUri();

    // Its companion local queue. This address is live on every node, which is the whole trap.
    private static readonly Uri TheCompanionQueue = "local://global-partition-slot-1".ToUri();

    // A local queue that is NOT part of a partitioned topology -- the control for unchanged behaviour.
    private static readonly Uri APlainLocalQueue = "local://plain-items".ToUri();

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<PartitionedRetryMessageHandler>();
                opts.PublishMessage<PartitionedRetryMessage>().To(TheSlot);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        // Exactly what GlobalPartitionedMessageTopology stamps on each external slot. Doing it by hand keeps
        // this test off a real broker; the property IS the contract the runtime reads.
        _host.GetRuntime().Endpoints.EndpointFor(TheSlot)!.GlobalPartitionLocalQueueUri = TheCompanionQueue;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static (Envelope, IMessageInbox) promotedEnvelope(Uri parkedAt)
    {
        var inbox = Substitute.For<IMessageInbox>();
        var store = Substitute.For<IMessageStore>();
        store.Inbox.Returns(inbox);

        var envelope = new Envelope(new PartitionedRetryMessage("due"))
        {
            // The address the row was written under, and therefore its received_at
            Destination = parkedAt,

            // The scheduled poller stamps the owning store on every envelope it recovers
            Store = store
        };

        return (envelope, inbox);
    }

    [Fact]
    public async Task a_non_owner_hands_the_retry_to_the_slot_rather_than_its_own_companion_queue()
    {
        var (envelope, _) = promotedEnvelope(TheCompanionQueue);

        var session = await _host.TrackActivity()
            .ExecuteAndWaitAsync(_ => _host.GetRuntime().EnqueueDirectlyAsync([envelope]).AsTask());

        session.Sent.Envelopes().ShouldContain(x => x.Id == envelope.Id && x.Destination == TheSlot);

        // The claim that matters: it was never run as the companion queue's message. (The stub transport
        // loops a send back into execution, which is what the owning node would legitimately do once the
        // envelope reached the slot -- so assert the address it executed under, not that nothing ran.)
        session.Executed.Envelopes().ShouldNotContain(x => x.Destination == TheCompanionQueue);
    }

    [Fact]
    public async Task the_forwarded_envelope_is_re_addressed_to_the_slot()
    {
        // Load-bearing. DurableReceiver stamps the listener's address only when the envelope has none
        // (Destination ??= Uri), so forwarding it untouched would park the row at the companion queue
        // address on the OWNING node too -- moving the bug rather than fixing it.
        var (envelope, _) = promotedEnvelope(TheCompanionQueue);

        await _host.GetRuntime().EnqueueDirectlyAsync([envelope]);

        envelope.Destination.ShouldBe(TheSlot);
    }

    [Fact]
    public async Task the_inbox_row_is_retired_at_the_address_it_was_parked_at()
    {
        // The other half of the ordering, and the GH-4645 data-loss shape if it is wrong. The delete matches
        // on id AND received_at, so it has to name the companion queue -- the address the row actually
        // carries -- not the slot the envelope was just re-addressed to.
        var (envelope, inbox) = promotedEnvelope(TheCompanionQueue);

        await _host.GetRuntime().EnqueueDirectlyAsync([envelope]);

        await inbox.Received().DeleteIncomingEnvelopeAsync(
            Arg.Is<Envelope>(x => x.Id == envelope.Id && x.Destination == TheCompanionQueue));

        await inbox.DidNotReceive().DeleteIncomingEnvelopeAsync(
            Arg.Is<Envelope>(x => x.Destination == TheSlot));
    }

    [Fact]
    public async Task a_plain_local_queue_is_still_handled_locally()
    {
        // The negative control. Only a global partition's companion queue is special; every other local
        // address has to keep taking the listener circuit, or this fix would divert ordinary local traffic.
        var (envelope, inbox) = promotedEnvelope(APlainLocalQueue);

        var session = await _host.TrackActivity()
            .ExecuteAndWaitAsync(_ => _host.GetRuntime().EnqueueDirectlyAsync([envelope]).AsTask());

        session.Executed.MessagesOf<PartitionedRetryMessage>().ShouldHaveSingleItem();

        // Settled by the receiver once actually handled, so nothing to retire here
        await inbox.DidNotReceive().DeleteIncomingEnvelopeAsync(Arg.Any<Envelope>());
    }
}

public record PartitionedRetryMessage(string Name);

public class PartitionedRetryMessageHandler
{
    public void Handle(PartitionedRetryMessage message)
    {
    }
}
