using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// The unit-level half of <c>PostgresqlTests.Transport.global_partition_slot_lifecycle_through_the_shard_queue</c>.
///
/// <para>
/// A global partition's companion local queue executes rows parked at two <c>received_at</c> addresses: its own,
/// for messages that took the local shortcut, and the slot's, for messages a durable database-backed slot popped
/// out of its shard queue table (GH-4288 writes those at the slot's address, owned by the popping node). The
/// GH-4777 handoff drains the companion queue and releases what it did not finish -- but
/// <c>DurableReceiver.DrainAsync</c> could only release its own address, so every un-run message that had come
/// through the shard queue stayed Incoming and owned by the ex-owner: a live node, invisible to the orphan sweep
/// and to the next owner's recovery loop alike. The receiver now carries the slot's address as a second release
/// target, on the same path as the first so the GH-4797 hold on in-flight work covers both.
/// </para>
/// </summary>
public class companion_queue_drain_releases_the_slot_address
{
    private static readonly Uri TheSlot = new("stub://partition-slot-1");

    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();

    private readonly TaskCompletionSource _handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public companion_queue_drain_releases_the_slot_address()
    {
        theRuntime.DurabilitySettings.DrainTimeout = 250.Milliseconds();
        theRuntime.DurabilitySettings.AssignedNodeNumber = 7;
    }

    private DurableReceiver buildCompanionReceiver()
    {
        return new DurableReceiver(new StubEndpoint("global-slot-1", new StubTransport()), theRuntime, thePipeline)
        {
            AlsoReleaseIncomingAt = TheSlot
        };
    }

    private Uri theCompanion(DurableReceiver receiver) => receiver.Uri;

    [Fact]
    public async Task a_clean_drain_releases_this_nodes_rows_at_both_addresses()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>()).Returns(Task.CompletedTask);

        var receiver = buildCompanionReceiver();

        receiver.Latch();
        await receiver.DrainAsync();

        await theRuntime.Storage.Inbox.Received().ReleaseIncomingAsync(7, theCompanion(receiver));
        await theRuntime.Storage.Inbox.Received().ReleaseIncomingAsync(7, TheSlot);
    }

    /// <summary>
    /// The negative control: an ordinary durable receiver has no second address and must not start releasing
    /// anything beyond its own.
    /// </summary>
    [Fact]
    public async Task a_receiver_outside_a_partition_releases_only_its_own_address()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>()).Returns(Task.CompletedTask);

        var receiver = new DurableReceiver(new StubEndpoint("plain", new StubTransport()), theRuntime, thePipeline);

        receiver.Latch();
        await receiver.DrainAsync();

        theRuntime.Storage.Inbox.ReceivedCalls()
            .Count(x => x.GetMethodInfo().Name == nameof(IMessageInbox.ReleaseIncomingAsync))
            .ShouldBe(1);

        await theRuntime.Storage.Inbox.Received().ReleaseIncomingAsync(7, receiver.Uri);
    }

    /// <summary>
    /// GH-4797 has to hold for the slot address too. A message that came through the shard queue and is still
    /// executing when the drain gives up is parked at the slot address; releasing that address while the
    /// handler runs would hand the row to the new owner and re-run it concurrently -- exactly the intra-group
    /// concurrency this whole mechanism exists to prevent.
    /// </summary>
    [Fact]
    public async Task a_timed_out_drain_holds_both_addresses_until_the_in_flight_handler_finishes()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>())
            .Returns(_ =>
            {
                _handlerEntered.TrySetResult();
                return _releaseHandler.Task;
            });

        var receiver = buildCompanionReceiver();

        var envelope = ObjectMother.Envelope();
        envelope.WasPersistedInInbox = true;
        envelope.Destination = TheSlot;
        await receiver.EnqueueAsync(envelope);
        await _handlerEntered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        receiver.Latch();
        await receiver.DrainAsync();

        await theRuntime.Storage.Inbox.DidNotReceive().ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>());

        _releaseHandler.TrySetResult();

        await waitForAsync(() => theRuntime.Storage.Inbox.ReceivedCalls()
                .Count(x => x.GetMethodInfo().Name == nameof(IMessageInbox.ReleaseIncomingAsync)) == 2,
            "The held rows were never released at both addresses after the in-flight handler finished");

        await theRuntime.Storage.Inbox.Received().ReleaseIncomingAsync(7, theCompanion(receiver));
        await theRuntime.Storage.Inbox.Received().ReleaseIncomingAsync(7, TheSlot);
    }

    /// <summary>
    /// The other receiver indirection on the slot path. The slot's ListeningAgent reads its depth off the bridge,
    /// and the slot's BackPressureAgent decides from that number whether to stop popping the shard queue -- so
    /// a bridge that reported 0 made back pressure unreachable for every global partition slot (the GH-4186
    /// finding, for the bridge instead of the interceptor).
    /// </summary>
    [Fact]
    public void the_bridge_reports_the_companion_queues_depth()
    {
        var companion = Substitute.For<ILocalQueue>();
        companion.QueueCount.Returns(42);

        var bridge = new GlobalPartitionedReceiverBridge(companion);

        ((IHasQueueDepth)bridge).QueueCount.ShouldBe(42);
    }

    private static async Task waitForAsync(Func<bool> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50.Milliseconds());
        }

        throw new TimeoutException(message);
    }
}
