using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// F4 and F5 from the durable compliance rig. A batched message's own execution ends the moment the batching
/// pipeline accepts it, but its inbox row stays Incoming and owned at the address it ARRIVED on until the batch it
/// joined finishes, usually on another queue. The receiver it arrived on let go of it at that first moment, so:
///
/// <para>F5: the InboxStaleTime heartbeat restarted the clock of the batch carrier, which has no row, and the
/// members' rows went stale and were released while the batch was still running.</para>
///
/// <para>F4: a drain of the receiver the members arrived on released them while their batch was still pending, and
/// a drain of the batch queue cancelled the pending batch retry and released nothing, stranding them on a live
/// node.</para>
///
/// <para>Members now stay in the custody of the receiver they arrived on until their batch is finished with
/// them.</para>
/// </summary>
public class batch_members_stay_in_custody_until_their_batch_settles : IAsyncLifetime
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly IHandlerPipeline theItemPipeline = Substitute.For<IHandlerPipeline>();
    private readonly IHandlerPipeline theBatchPipeline = Substitute.For<IHandlerPipeline>();
    private readonly StubEndpoint theItemEndpoint = new("items", new StubTransport());
    private readonly StubEndpoint theBatchEndpoint = new("batches", new StubTransport());
    private readonly List<DurableReceiver> _receivers = new();

    private DurableReceiver theItemReceiver = null!;
    private DurableReceiver theBatchReceiver = null!;

    public batch_members_stay_in_custody_until_their_batch_settles()
    {
        // The item handler is the batching pipeline: it accepts the envelope as a member and returns
        theItemPipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>())
            .Returns(call =>
            {
                call.Arg<Envelope>().InBatch = true;
                return Task.CompletedTask;
            });
    }

    public ValueTask InitializeAsync()
    {
        buildReceivers();
        return ValueTask.CompletedTask;
    }

    private void buildReceivers()
    {
        theItemReceiver = new DurableReceiver(theItemEndpoint, theRuntime, theItemPipeline);
        theBatchReceiver = new DurableReceiver(theBatchEndpoint, theRuntime, theBatchPipeline);
        _receivers.Add(theItemReceiver);
        _receivers.Add(theBatchReceiver);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var receiver in _receivers) receiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    private async Task<Envelope[]> membersAreAcceptedIntoABatch(int count)
    {
        var members = Enumerable.Range(0, count).Select(_ =>
        {
            var member = ObjectMother.Envelope();
            member.Destination = theItemEndpoint.Uri;
            member.WasPersistedInInbox = true;
            return member;
        }).ToArray();

        foreach (var member in members) await theItemReceiver.EnqueueAsync(member);

        await waitForAsync(() => theItemReceiver.HeldBatchMemberCount == count && theItemReceiver.QueueCount == 0,
            "The members were not held by the receiver they arrived on after the batching pipeline accepted them");

        return members;
    }

    private static Envelope aCarrierFor(Envelope[] members) => new(new[] { "batch" }, members);

    private static async Task waitForAsync(Func<bool> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25.Milliseconds());
        }

        throw new TimeoutException(message);
    }

    [Fact]
    public async Task a_member_is_held_by_the_receiver_it_arrived_on_until_its_batch_completes()
    {
        var members = await membersAreAcceptedIntoABatch(3);

        await theBatchReceiver.CompleteAsync(aCarrierFor(members));

        theItemReceiver.HeldBatchMemberCount.ShouldBe(0);

        // Marked one at a time or coalesced into one flush, every member's row is marked handled
        var marked = theRuntime.Storage.Inbox.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IMessageInbox.MarkIncomingEnvelopeAsHandledAsync))
            .SelectMany(x => x.GetArguments()[0] switch
            {
                Envelope one => [one],
                IReadOnlyList<Envelope> many => many,
                _ => Array.Empty<Envelope>()
            })
            .ToArray();

        members.ShouldAllBe(x => marked.Contains(x));
    }

    [Fact]
    public async Task a_member_dead_lettered_by_its_batch_is_no_longer_held()
    {
        var members = await membersAreAcceptedIntoABatch(2);

        foreach (var member in members)
        {
            await theBatchReceiver.MoveToErrorsAsync(member, new DivideByZeroException());
        }

        theItemReceiver.HeldBatchMemberCount.ShouldBe(0);
    }

    [Fact]
    public async Task draining_the_receiver_members_arrived_on_does_not_release_them_while_their_batch_is_pending()
    {
        var members = await membersAreAcceptedIntoABatch(3);

        theItemReceiver.Latch();
        await theItemReceiver.DrainAsync();

        await theRuntime.Storage.Inbox.DidNotReceive().ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>());
        await theRuntime.Storage.DidNotReceive()
            .ReassignIncomingAsync(Arg.Any<int>(), Arg.Is<IReadOnlyList<Envelope>>(x => x.Any(members.Contains)));

        // The batch still settles them afterwards
        await theBatchReceiver.CompleteAsync(aCarrierFor(members));
        theItemReceiver.HeldBatchMemberCount.ShouldBe(0);
    }

    [Fact]
    public async Task draining_the_batch_queue_with_a_retry_pending_hands_the_members_back()
    {
        var members = await membersAreAcceptedIntoABatch(3);
        var carrier = aCarrierFor(members);

        await theBatchReceiver.MoveToScheduledUntilAsync(carrier, DateTimeOffset.UtcNow.AddMinutes(5));

        theBatchReceiver.Latch();
        await theBatchReceiver.DrainAsync();

        // Released by id at the address each member arrived on, so recovery runs them again
        await theRuntime.Storage.Received().ReassignIncomingAsync(TransportConstants.AnyNode,
            Arg.Is<IReadOnlyList<Envelope>>(x => members.All(x.Contains)));

        theItemReceiver.HeldBatchMemberCount.ShouldBe(0);
    }

    [Fact]
    public async Task the_heartbeat_restarts_the_clock_of_a_member_waiting_on_its_batch()
    {
        // A heartbeat-capable inbox and a short stale time: the heartbeat period is a third of it
        var heartbeatInbox = Substitute.For<IMessageInbox, IMessageInboxHeartbeat>();
        theRuntime.Storage.Inbox.Returns(heartbeatInbox);
        theRuntime.DurabilitySettings.InboxStaleTime = 300.Milliseconds();
        buildReceivers();

        var members = await membersAreAcceptedIntoABatch(2);

        await waitForAsync(() => ((IMessageInboxHeartbeat)heartbeatInbox).ReceivedCalls()
                .Select(x => (IReadOnlyList<Envelope>)x.GetArguments()[1]!)
                .Any(x => members.All(x.Contains)),
            "The heartbeat never restarted the clock of members still waiting on their batch");

        // And it stops once the batch has finished with them
        await theBatchReceiver.CompleteAsync(aCarrierFor(members));
        theItemReceiver.HeldBatchMemberCount.ShouldBe(0);
    }

    /// <summary>
    /// The same-queue topology: items and their batches share one durable local queue, so the receiver the members
    /// arrived on is the one that pauses. Its drain must not release members still waiting on their batch, and a
    /// carrier the paused queue held and handed to its NEXT receiver on resume still settles them.
    /// </summary>
    [Fact]
    public async Task pausing_a_queue_that_batches_its_own_items_keeps_the_members_until_the_resumed_batch_settles()
    {
        var members = await membersAreAcceptedIntoABatch(3);

        theItemReceiver.Latch();
        await theItemReceiver.DrainAsync();

        await theRuntime.Storage.Inbox.DidNotReceive().ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>());
        await theRuntime.Storage.DidNotReceive()
            .ReassignIncomingAsync(Arg.Any<int>(), Arg.Is<IReadOnlyList<Envelope>>(x => x.Any(members.Contains)));
        theItemReceiver.HeldBatchMemberCount.ShouldBe(3);

        // The queue resumes with a new receiver, and the held carrier runs there
        var resumed = new DurableReceiver(theItemEndpoint, theRuntime, theBatchPipeline);
        _receivers.Add(resumed);
        await resumed.ClearInFlightIncomingAsync();
        await theRuntime.Storage.Inbox.DidNotReceive().ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>());

        await resumed.CompleteAsync(aCarrierFor(members));

        theItemReceiver.HeldBatchMemberCount.ShouldBe(0);
    }

    /// <summary>
    /// An enqueue that read the old receiver just before a paused queue swapped it out posts into a drained receiver,
    /// whose completed block dropped it in memory and left its row owned by this node. It is handed back instead.
    /// </summary>
    [Fact]
    public async Task an_envelope_posted_into_a_drained_receiver_is_handed_back_rather_than_dropped()
    {
        theItemReceiver.Latch();
        await theItemReceiver.DrainAsync();

        var late = ObjectMother.Envelope();
        late.Destination = theItemEndpoint.Uri;
        await theItemReceiver.EnqueueAsync(late);

        await waitForAsync(() => theRuntime.Storage.ReceivedCalls().Any(x =>
                x.GetMethodInfo().Name == nameof(IMessageStore.ReassignIncomingAsync) &&
                ((IReadOnlyList<Envelope>)x.GetArguments()[1]!).Contains(late)),
            "The envelope posted after the drain was dropped and its row never handed back");
    }

    /// <summary>
    /// A batch on the default, buffered queue settles its members in BatchMemberInbox, which only clears InBatch. That
    /// is a terminal too: the receiver the members arrived on must stop holding them, or its address would look busy
    /// forever and its drains could never use the blanket release again.
    /// </summary>
    [Fact]
    public async Task a_member_whose_batch_terminal_only_clears_in_batch_is_no_longer_held()
    {
        var members = await membersAreAcceptedIntoABatch(2);

        foreach (var member in members) member.InBatch = false;

        theItemReceiver.HeldBatchMemberCount.ShouldBe(0);

        theItemReceiver.Latch();
        await theItemReceiver.DrainAsync();
        await theRuntime.Storage.Inbox.Received().ReleaseIncomingAsync(Arg.Any<int>(), theItemEndpoint.Uri);
    }
}
