using System.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
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
                    Enumerable.Range(0, 60).Select(_ => pingFor("one-hot-group")).ToArray()),
                TestContext.Current.CancellationToken);

            await waitUntil(() => snapshotFor(agent.Uri).BusiestLaneCount > 1,
                "the partitioned listener never reported a busiest-lane depth");

            var snapshot = snapshotFor(agent.Uri);

            snapshot.LaneCount.ShouldBe(5);

            // The assertion the aggregate could never make: nearly everything is in ONE lane.
            snapshot.BusiestLaneCount.ShouldNotBeNull();
            snapshot.BusiestLaneCount.Value.ShouldBeGreaterThan(1);
            snapshot.BusiestLaneCount!.Value.ShouldBeGreaterThan(snapshot.QueueCount / 5,
                "All of these messages share a group id, so the busiest lane must hold far more than an even share.");

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
            var flood = Task.Run(() => agent.EnqueueDirectlyAsync(
                    Enumerable.Range(0, 60).Select(i => pingFor("group-" + i)).ToArray()),
                TestContext.Current.CancellationToken);

            await waitUntil(() => snapshotFor(agent.Uri).QueueCount > 5,
                "the partitioned listener never took on any depth");

            var snapshot = snapshotFor(agent.Uri);

            // Distinct group ids hash across the slots, so no single lane holds the whole load.
            snapshot.BusiestLaneCount.ShouldNotBeNull();
            snapshot.BusiestLaneCount.Value.ShouldBeLessThan(snapshot.QueueCount,
                "With 60 distinct group ids no single lane should hold everything -- if it does, this is the aggregate wearing a different name.");

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
