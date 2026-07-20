using System.Diagnostics;
using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Regression coverage for GH-3518. In DurabilityMode.Solo the recurring health-check
/// loop is launched with a fire-and-forget Task.Run inside StartSoloModeAsync. Before the
/// fix that Task.Run captured the ambient ExecutionContext — including the AsyncLocal
/// behind Activity.Current — while the startup "wolverine_node_assignments" activity was
/// still current. Every subsequent loop iteration (and every DB call underneath it) then
/// reparented itself to that one stopped-but-still-ambient activity for the entire process
/// lifetime, producing a single OTel trace that grew without bound.
///
/// The fix suppresses ExecutionContext flow when scheduling the loop and starts a fresh,
/// bounded activity per tick — so each tick is its own root trace, and ShouldTraceHealthCheck()
/// (hence NodeAssignmentHealthCheckTraceSamplingPeriod) is finally honored in Solo mode.
/// </summary>
public class solo_mode_health_check_tracing : IDisposable
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly NodeAgentController _controller;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Activity> _captured = new();
    private readonly ActivityListener _listener;

    public solo_mode_health_check_tracing()
    {
        _options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        _options.Durability.Mode = DurabilityMode.Solo;
        _options.Durability.DurabilityAgentEnabled = false; // skip MessageStoreCollection wiring
        // Fast ticks so the recurring loop fires several times within the test window.
        _options.Durability.CheckAssignmentPeriod = 25.Milliseconds();

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "wolverine_node_assignments")
                {
                    lock (_captured)
                    {
                        _captured.Add(activity);
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(_listener);

        _controller = new NodeAgentController(
            _runtime,
            Substitute.For<INodeAgentPersistence>(),
            Array.Empty<IAgentFamily>(),
            NullLogger<NodeAgentController>.Instance,
            _cancellation.Token);
    }

    private Activity[] snapshot()
    {
        lock (_captured)
        {
            return _captured.ToArray();
        }
    }

    [Fact]
    public async Task each_recurring_tick_is_its_own_bounded_root_trace()
    {
        await _controller.StartSoloModeAsync();

        // Let the recurring loop fire several times (25ms period).
        await Task.Delay(400.Milliseconds());

        // Stop the loop before asserting.
        await _cancellation.CancelAsync();

        var activities = snapshot();

        // The startup activity plus at least a couple of recurring-loop ticks. Before the
        // fix the loop never started a fresh activity at all, so only the single startup
        // "wolverine_node_assignments" activity was ever produced.
        activities.Length.ShouldBeGreaterThan(2,
            "the recurring Solo-mode health-check loop must start a fresh activity per tick");

        // The core GH-3518 invariant: every tick is its own root trace, never nesting under
        // a long-lived ambient parent. If the loop reparents to the captured startup context
        // (the bug), the tick activities would share a TraceId and/or carry a non-null Parent.
        foreach (var activity in activities)
        {
            activity.Parent.ShouldBeNull(
                "each Solo-mode health-check activity must be a root, not nested under a leaked ambient parent");
        }

        activities.Select(a => a.TraceId).Distinct().Count()
            .ShouldBe(activities.Length, "each Solo-mode health-check tick must get its own bounded trace");
    }

    [Fact]
    public async Task sampling_period_throttles_solo_mode_recurring_checks()
    {
        // NodeAssignmentHealthCheckTraceSamplingPeriod was a no-op in Solo mode before the fix
        // because ShouldTraceHealthCheck() was only evaluated once, at startup. Now it gates
        // every tick, so a long sampling period must suppress the per-tick traces.
        _options.Durability.NodeAssignmentHealthCheckTraceSamplingPeriod = 1.Hours();

        await _controller.StartSoloModeAsync();
        await Task.Delay(400.Milliseconds());
        await _cancellation.CancelAsync();

        // Only the startup tick should trace within a 1-hour sampling window.
        snapshot().Length.ShouldBe(1,
            "a long sampling period must throttle the recurring Solo-mode health checks");
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Dispose();
        _cancellation.Dispose();
    }
}
