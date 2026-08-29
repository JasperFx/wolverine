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
/// GH-4186. ListeningAgent read the receiver's depth through ILocalQueue, and neither NativeAckReceiver nor
/// InlineReceiver is one -- deliberately, because a delivery in those two modes settles against the listener that
/// brought it rather than against a local queue. Both maintain a real depth over a real block, and both used to
/// contribute a constant 0 to EndpointHealthSnapshot: a saturated NativeAck listener was indistinguishable from
/// an idle one, and rendered downstream as a reassuring green zero rather than as "unknown".
///
/// The companion half is LastQueueActivityAt, whose change-detection heuristic has exactly one writer --
/// BackPressureAgent -- which correctly does not run for either mode (see Endpoint.ShouldEnforceBackPressure),
/// leaving the timestamp frozen at listener construction forever.
/// </summary>
public class native_ack_queue_depth_4186 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<NativeAckPingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
        NativeAckPingHandler.Handled.Clear();
        NativeAckPingHandler.Gate = null;
        NativeAckPingHandler.Entered = null;
    }

    public async ValueTask DisposeAsync()
    {
        NativeAckPingHandler.Gate = null;
        NativeAckPingHandler.Entered = null;
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task depth_and_receipt_activity_reach_the_endpoint_health_snapshot()
    {
        var endpoint = new NativeAckStubEndpoint("na-4186", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);
        var agent = theRuntime.Endpoints.FindListeningAgent(endpoint.Uri).ShouldNotBeNull();

        // Guard against a vacuous pass: a BufferedReceiver here already reported its depth before GH-4186,
        // and the test would prove nothing.
        receiverOf(agent).ShouldBeOfType<NativeAckReceiver>();

        var idle = snapshotFor(endpoint.Uri);
        idle.QueueCount.ShouldBe(0);

        // Not a wait on anything -- pure clock separation, so that the "moved" assertion below cannot pass or
        // fail on DateTimeOffset.UtcNow's granularity (~15ms on Windows) rather than on the fix.
        await Task.Delay(50.Milliseconds(), TestContext.Current.CancellationToken);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeAckPingHandler.Gate = gate;

        try
        {
            // Deliberately not awaited: with every handler parked on the gate, a large enough batch will fill
            // the block's bounded channel and the post itself will block. Backgrounding it keeps this test
            // agnostic about that capacity.
            var flood = Task.Run(() => agent.EnqueueDirectlyAsync(
                Enumerable.Range(0, 200).Select(i => pingEnvelope("flood-" + i)).ToArray()), TestContext.Current.CancellationToken);

            await waitUntil(() => snapshotFor(endpoint.Uri).QueueCount > 0,
                "the NativeAck listener never reported a non-zero QueueCount");

            var saturated = snapshotFor(endpoint.Uri);

            // The whole point of the issue: the depth is real, and it is now visible.
            saturated.QueueCount.ShouldBeGreaterThan(0);

            // ...and the listener no longer looks like it has been idle since boot.
            saturated.LastQueueActivityAt.ShouldNotBeNull();
            saturated.LastQueueActivityAt.Value.ShouldBeGreaterThan(idle.LastQueueActivityAt!.Value);

            gate.SetResult();
            await flood;
        }
        finally
        {
            gate.TrySetResult();
        }

        // And it is a live number rather than a one-time stamp -- it comes back down as the block drains.
        await waitUntil(() => snapshotFor(endpoint.Uri).QueueCount == 0,
            "the NativeAck listener's reported QueueCount never returned to zero");
    }

    [Fact]
    public async Task inline_receiver_depth_reaches_the_snapshot_too()
    {
        var endpoint = new StubEndpoint("inline-4186", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.Inline;

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);
        var agent = theRuntime.Endpoints.FindListeningAgent(endpoint.Uri).ShouldNotBeNull();
        receiverOf(agent).ShouldBeOfType<InlineReceiver>();

        snapshotFor(endpoint.Uri).QueueCount.ShouldBe(0);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeAckPingHandler.Gate = gate;
        NativeAckPingHandler.Entered = entered;

        try
        {
            // Inline invokes the pipeline on the caller's stack, so this cannot be awaited before asserting.
            var inFlight = Task.Run(() => agent.EnqueueDirectlyAsync([pingEnvelope("inline")]), TestContext.Current.CancellationToken);

            await entered.Task.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);

            // Inline has no queue, but it does have in-flight work, and that is the number an operator wants.
            snapshotFor(endpoint.Uri).QueueCount.ShouldBe(1);

            gate.SetResult();
            await inFlight;
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    /// <summary>
    /// Shape 1 in the issue -- making NativeAckReceiver implement ILocalQueue -- would have been the smaller
    /// diff and the wrong one: ListeningAgent.EnqueueDirectlyAsync type-switches on ILocalQueue *before* it
    /// reaches the NativeAck branch that GH-4011 added, so a NativeAck receiver claiming to be a local queue
    /// would silently take the wrong path on every DLQ replay.
    /// </summary>
    [Fact]
    public void a_native_ack_receiver_reports_a_depth_without_claiming_to_be_a_local_queue()
    {
        var endpoint = new NativeAckStubEndpoint("na-4186-shape", new StubTransport());
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        var receiver = new NativeAckReceiver(endpoint, theRuntime,
            new HandlerPipeline(theRuntime, theRuntime, endpoint));

        receiver.ShouldBeAssignableTo<IHasQueueDepth>();
        receiver.ShouldNotBeAssignableTo<ILocalQueue>();
    }

    private EndpointHealthSnapshot snapshotFor(Uri uri)
    {
        return theRuntime.Endpoints.CollectEndpointHealth()
            .Single(x => x.Uri == uri && x.Direction == EndpointDirection.Listening);
    }

    private static Envelope pingEnvelope(string name)
    {
        return new Envelope(new NativeAckPing(name)) { MessageType = typeof(NativeAckPing).ToMessageTypeName() };
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
