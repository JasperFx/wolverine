using System.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4199, item 2. A partitioned listener's whole point is its slots -- one single-worker lane per slot, so
/// same-group messages serialize while different groups run in parallel -- and the only depth it exposed was
/// the sum. 100 messages spread evenly over 10 lanes and 100 messages piled into one lane reported the
/// identical number, so the failure the structure exists to bound ("one dominant GroupId serializes
/// everything behind it", the one GH-3899's exempt lane mitigates) was invisible from outside the process.
/// </summary>
public class partitioned_lane_depth_4199 : IAsyncLifetime
{
    private const int MessageCount = 60;

    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<LaneDepthPingHandler>();
                opts.MessagePartitioning.ByMessage<LaneDepthPing>(x => x.Group);
            }).StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
        LaneDepthPingHandler.Gate = null;
    }

    public async ValueTask DisposeAsync()
    {
        LaneDepthPingHandler.Gate?.TrySetResult();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_hot_lane_is_distinguishable_from_an_evenly_loaded_one()
    {
        var agent = await startPartitionedListenerAsync("lanes-4199");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LaneDepthPingHandler.Gate = gate;

        try
        {
            // Every message carries the SAME group id, so the partitioner routes all of them to one slot.
            var flood = Task.Run(() => agent.EnqueueDirectlyAsync(
                    Enumerable.Range(0, MessageCount).Select(_ => pingFor("one-hot-group")).ToArray()),
                TestContext.Current.CancellationToken);

            var snapshot = await waitForEveryMessageToReachALaneAsync(agent);

            snapshot.QueueCount.ShouldBe(MessageCount);
            snapshot.LaneCount.ShouldBe(5);

            // The assertion the aggregate could never make: everything is in ONE lane.
            snapshot.BusiestLaneCount.ShouldNotBeNull();
            snapshot.BusiestLaneCount.Value.ShouldBe(snapshot.QueueCount,
                "All of these messages share a group id, so the busiest lane must hold every one of them.");

            gate.SetResult();
            await flood;
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    /// <summary>
    /// The control. Without this the busiest-lane number could be the aggregate under another name and the
    /// test above would still pass.
    /// </summary>
    [Fact]
    public async Task spread_traffic_does_not_report_a_hot_lane()
    {
        var agent = await startPartitionedListenerAsync("lanes-4199-spread");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LaneDepthPingHandler.Gate = gate;

        try
        {
            var pings = Enumerable.Range(0, MessageCount).Select(i => pingFor("group-" + i)).ToArray();

            // The slot hash is deterministic, so the depth of the deepest lane is known before anything is sent
            var expectedBusiest = pings
                .GroupBy(x => x.SlotForProcessing(5, theRuntime.Options.MessagePartitioning))
                .Max(x => x.Count());

            // Guard against a vacuous control: if these group ids all hashed to one slot this would prove nothing
            expectedBusiest.ShouldBeLessThan(MessageCount);

            var flood = Task.Run(() => agent.EnqueueDirectlyAsync(pings), TestContext.Current.CancellationToken);

            var snapshot = await waitForEveryMessageToReachALaneAsync(agent);

            snapshot.QueueCount.ShouldBe(MessageCount);

            // Distinct group ids hash across the slots, so no single lane holds the whole load -- and the number
            // reported is the deepest lane itself, not the sum.
            snapshot.BusiestLaneCount.ShouldBe(expectedBusiest,
                $"With {MessageCount} distinct group ids no single lane should hold everything -- if it does, this is the aggregate wearing a different name.");

            gate.SetResult();
            await flood;
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task an_unpartitioned_listener_reports_no_lanes_at_all()
    {
        var endpoint = new NativeAckStubEndpoint("nolanes-4199", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);

        var snapshot = snapshotFor(endpoint.Uri);

        // Null rather than 1, because "not partitioned" and "partitioned into one lane" are different states.
        snapshot.LaneCount.ShouldBeNull();
        snapshot.BusiestLaneCount.ShouldBeNull();
        snapshot.ExemptLaneCount.ShouldBeNull();
    }

    /// <summary>
    /// The trap this design had to survive: ReceiverWithRules is installed by something as ordinary as an
    /// endpoint-level MessageType, and GlobalPartitionedInterceptor can nest on top of it. A default
    /// interface member on IHasQueueDepth would silently report "not partitioned" through either one.
    /// </summary>
    [Fact]
    public async Task lane_depth_survives_a_receiver_wrapper()
    {
        var endpoint = new NativeAckStubEndpoint("lanes-4199-wrapped", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.GroupShardingSlotNumber = PartitionSlots.Five;

        // This alone installs ReceiverWithRules in front of the NativeAckReceiver.
        endpoint.MessageType = typeof(LaneDepthPing);

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);
        var agent = theRuntime.Endpoints.FindListeningAgent(endpoint.Uri).ShouldNotBeNull();

        // Guard against a vacuous pass: if no wrapper is installed this proves nothing about delegation.
        receiverOf(agent).ShouldBeOfType<ReceiverWithRules>();

        snapshotFor(endpoint.Uri).LaneCount.ShouldBe(5,
            "The wrapper has to delegate LaneDepth, or a partitioned endpoint reads as unpartitioned for most real configurations.");
    }

    private async Task<IListeningAgent> startPartitionedListenerAsync(string name)
    {
        var endpoint = new NativeAckStubEndpoint(name, new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.GroupShardingSlotNumber = PartitionSlots.Five;

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);
        return theRuntime.Endpoints.FindListeningAgent(endpoint.Uri).ShouldNotBeNull();
    }

    /// <summary>
    /// Waits for a STATE, not for time. A partitioned NativeAck receiver puts an ordered deserialization stage in
    /// front of its lanes -- the group id, and so the lane, is only known once the message is deserialized -- and
    /// QueueCount counts that stage too, because its messages are work not yet finished. While it is still
    /// draining, the snapshot honestly reports most of the load as queued but not yet in any lane, so a snapshot
    /// taken the moment the first lane shows any depth (what this test used to do) could read 2 of 60 in the hot
    /// lane on a loaded machine. The handler is gated, so nothing leaves a lane: once every message is in one and
    /// the upstream stage is empty, the snapshot is stable and the assertions are exact.
    /// </summary>
    private async Task<EndpointHealthSnapshot> waitForEveryMessageToReachALaneAsync(IListeningAgent agent)
    {
        var receiver = receiverOf(agent).Unwrap().ShouldBeOfType<NativeAckReceiver>();
        var lanes = (ShardedExecutionBlock)typeof(NativeAckReceiver)
            .GetField("_sharded", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(receiver).ShouldNotBeNull();

        EndpointHealthSnapshot? snapshot = null;
        await waitUntil(() =>
            {
                if (lanes.Count != MessageCount) return false;

                // Read after the lanes are full: an item is briefly counted by both the stage handing it on and the
                // lane receiving it, so the total settles at MessageCount only once the upstream stage is empty
                snapshot = snapshotFor(agent.Uri);
                return snapshot.QueueCount == MessageCount;
            },
            $"Not every one of the {MessageCount} messages reached a partition lane");

        return snapshot!;
    }

    private EndpointHealthSnapshot snapshotFor(Uri uri)
    {
        return theRuntime.Endpoints.CollectEndpointHealth()
            .Single(x => x.Uri == uri && x.Direction == EndpointDirection.Listening);
    }

    private static Envelope pingFor(string group)
    {
        return new Envelope(new LaneDepthPing(group))
        {
            MessageType = typeof(LaneDepthPing).ToMessageTypeName()
        };
    }

    private static async Task waitUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException(failure);
    }

    private static IReceiver receiverOf(IListeningAgent agent)
    {
        var field = typeof(ListeningAgent).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IReceiver)field.GetValue(agent)!;
    }
}

public record LaneDepthPing(string Group);

public class LaneDepthPingHandler
{
    public static TaskCompletionSource? Gate { get; set; }

    public static async Task Handle(LaneDepthPing ping)
    {
        var gate = Gate;
        if (gate != null)
        {
            await gate.Task;
        }
    }
}
