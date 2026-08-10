using System.Collections.Immutable;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace MetricsTests;

/// <summary>
/// CritterWatch #963 phase 4 — the upstream half of "99.6% of persisted metric samples are
/// all-zero rows". An accumulator window that saw no activity must export nothing (the
/// MetricsAccumulator XML doc always claimed this; now it is true), and a tenant idle for a
/// bounded number of export cycles must be evicted from tracking rather than emitting a zero
/// row forever.
/// </summary>
public class empty_snapshot_suppression_and_idle_tenant_eviction
{
    private static MessageTypeMetricsAccumulator theAccumulator()
        => new("m1", new Uri("stub://one"));

    // Applies activity synchronously through the public Process entry rather than the batching
    // pipeline: Block.WaitForCompletionAsync() permanently completes the pipeline, so tests that
    // span multiple accumulation windows cannot drain it more than once.
    private static void publishExecution(MessageTypeMetricsAccumulator accumulator, string tenantId)
    {
        accumulator.Process([new RecordExecutionTime(100, tenantId)]);
    }

    [Fact]
    public void export_with_no_activity_at_all_is_empty()
    {
        var accumulator = theAccumulator();

        var metrics = accumulator.TriggerExport(1);

        metrics.IsEmpty.ShouldBeTrue();
        metrics.PerTenant.ShouldBeEmpty();
    }

    [Fact]
    public void tracked_but_idle_tenant_contributes_no_zero_row()
    {
        var accumulator = theAccumulator();
        publishExecution(accumulator, "t1");

        // First export carries the activity
        var first = accumulator.TriggerExport(1);
        first.IsEmpty.ShouldBeFalse();
        first.PerTenant.Single().TenantId.ShouldBe("t1");

        // The tenant is still tracked, but the next window saw nothing — no zero row
        var second = accumulator.TriggerExport(1);
        second.IsEmpty.ShouldBeTrue();
        second.PerTenant.ShouldBeEmpty();
        accumulator.Counts.PerTenant.Contains("t1").ShouldBeTrue();
    }

    [Fact]
    public void snapshot_contains_only_the_tenants_with_activity()
    {
        var accumulator = theAccumulator();
        publishExecution(accumulator, "t1");
        publishExecution(accumulator, "t2");

        // both exported once
        accumulator.TriggerExport(1).PerTenant.Select(x => x.TenantId).ShouldBe(["t1", "t2"]);

        // only t2 is active in the next window; t1 stays tracked but emits nothing
        publishExecution(accumulator, "t2");
        var metrics = accumulator.TriggerExport(1);
        metrics.PerTenant.Single().TenantId.ShouldBe("t2");
        accumulator.Counts.PerTenant.Contains("t1").ShouldBeTrue();
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("effective")]
    [InlineData("sent")]
    [InlineData("received")]
    [InlineData("failure")]
    [InlineData("dead_letter")]
    public async Task any_single_kind_of_activity_makes_the_snapshot_non_empty(string kind)
    {
        var accumulator = theAccumulator();

        IHandlerMetricsData data = kind switch
        {
            "execution" => new RecordExecutionTime(50, "t1"),
            "effective" => new RecordEffectiveTime(50.5, "t1"),
            "sent" => new RecordSent("t1", "stub://source"),
            "received" => new RecordReceived("t1", "stub://source"),
            "failure" => new RecordFailure(typeof(DivideByZeroException).FullNameInCode(), "t1"),
            "dead_letter" => new RecordDeadLetter(typeof(DivideByZeroException).FullNameInCode(), "t1"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        await accumulator.EntryPoint.PostAsync(data);
        await accumulator.EntryPoint.WaitForCompletionAsync();

        var metrics = accumulator.TriggerExport(1);
        metrics.IsEmpty.ShouldBeFalse();
        metrics.PerTenant.Single().TenantId.ShouldBe("t1");

        // and the window after it is empty again
        accumulator.TriggerExport(1).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void tenant_is_evicted_after_the_configured_number_of_idle_export_cycles()
    {
        var accumulator = theAccumulator();
        publishExecution(accumulator, "t1");

        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3).IsEmpty.ShouldBeFalse();

        // two idle cycles — still tracked
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);
        accumulator.Counts.PerTenant.Contains("t1").ShouldBeTrue();

        // third consecutive idle cycle — evicted
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);
        accumulator.Counts.PerTenant.Contains("t1").ShouldBeFalse();
    }

    [Fact]
    public void evicted_tenant_is_retracked_on_new_activity()
    {
        var accumulator = theAccumulator();
        publishExecution(accumulator, "t1");
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 1);

        // one idle cycle at threshold 1 evicts immediately
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 1);
        accumulator.Counts.PerTenant.Contains("t1").ShouldBeFalse();

        // new activity re-creates the tracking entry and exports normally
        publishExecution(accumulator, "t1");
        accumulator.Counts.PerTenant.Contains("t1").ShouldBeTrue();

        var metrics = accumulator.TriggerExport(1, idleTenantEvictionCycles: 1);
        metrics.PerTenant.Single().TenantId.ShouldBe("t1");
        metrics.PerTenant.Single().Executions.Count.ShouldBe(1);
    }

    [Fact]
    public void new_activity_resets_the_idle_cycle_count()
    {
        var accumulator = theAccumulator();
        publishExecution(accumulator, "t1");
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);

        // two idle cycles...
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);

        // ...then activity resets the clock...
        publishExecution(accumulator, "t1");
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);

        // ...so two more idle cycles do NOT evict
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);
        accumulator.Counts.PerTenant.Contains("t1").ShouldBeTrue();

        // but the third does
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 3);
        accumulator.Counts.PerTenant.Contains("t1").ShouldBeFalse();
    }

    [Fact]
    public void zero_or_negative_threshold_disables_eviction()
    {
        var accumulator = theAccumulator();
        publishExecution(accumulator, "t1");
        accumulator.TriggerExport(1, idleTenantEvictionCycles: 0);

        for (var i = 0; i < 50; i++)
        {
            accumulator.TriggerExport(1, idleTenantEvictionCycles: 0);
        }

        accumulator.Counts.PerTenant.Contains("t1").ShouldBeTrue();

        for (var i = 0; i < 50; i++)
        {
            accumulator.TriggerExport(1, idleTenantEvictionCycles: -1);
        }

        accumulator.Counts.PerTenant.Contains("t1").ShouldBeTrue();
    }

    [Fact]
    public async Task background_export_loop_publishes_activity_but_not_idle_windows()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Metrics.Mode = WolverineMetricsMode.CritterWatch;
                opts.Metrics.SamplingPeriod = 250.Milliseconds();
                opts.OnAnyException().RetryTimes(3).Then.MoveToErrorQueue();
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var observer = new InstanceCapturingObserver();
        runtime.Observer = observer;

        var bus = host.MessageBus();
        for (var i = 0; i < 10; i++)
        {
            await bus.PublishAsync(new M1(Guid.CreateVersion7()));
        }

        // the export tick following the activity publishes a non-empty snapshot
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (observer.Collected.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        observer.Collected.ShouldNotBeEmpty();
        observer.Collected.ShouldAllBe(x => !x.IsEmpty);

        // now go quiet: several full sampling periods with no traffic publish nothing at all,
        // even though the accumulator for M1 still exists
        await Task.Delay(500, TestContext.Current.CancellationToken);
        observer.Clear();
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        observer.Collected.ShouldBeEmpty();
    }

    /// <summary>
    /// Instance-scoped capture (unlike the static <see cref="MetricsCollectionHandler"/>) so this
    /// class's host-level assertions cannot be polluted by other test classes running in parallel.
    /// </summary>
    private class InstanceCapturingObserver : IWolverineObserver
    {
        private readonly object _lock = new();

        public ImmutableArray<MessageHandlingMetrics> Collected { get; private set; }
            = ImmutableArray<MessageHandlingMetrics>.Empty;

        public void Clear()
        {
            lock (_lock)
            {
                Collected = ImmutableArray<MessageHandlingMetrics>.Empty;
            }
        }

        public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics)
        {
            lock (_lock)
            {
                Collected = Collected.Add(metrics);
            }
        }

        public Task AssumedLeadership() => Task.CompletedTask;
        public Task NodeStarted() => Task.CompletedTask;
        public Task NodeStopped() => Task.CompletedTask;
        public Task AgentStarted(Uri agentUri) => Task.CompletedTask;
        public Task AgentStopped(Uri agentUri) => Task.CompletedTask;
        public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands) => Task.CompletedTask;
        public Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes) => Task.CompletedTask;
        public Task RuntimeIsFullyStarted() => Task.CompletedTask;
        public void EndpointAdded(Endpoint endpoint) { }
        public void MessageRouted(Type messageType, IMessageRouter router) { }
        public Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent) => Task.CompletedTask;
        public Task BackPressureLifted(Endpoint endpoint) => Task.CompletedTask;
        public Task ListenerLatched(Endpoint endpoint) => Task.CompletedTask;
        public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options) => Task.CompletedTask;
        public Task CircuitBreakerReset(Endpoint endpoint) => Task.CompletedTask;
        public void PersistedCounts(Uri storeUri, PersistedCounts counts) { }
    }
}
