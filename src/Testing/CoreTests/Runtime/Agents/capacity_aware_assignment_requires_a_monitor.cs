using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4589. Capacity-aware assignment has no default load monitor on purpose: what "load" means is
/// specific to what the application does, and a built-in guess looks authoritative while being wrong
/// for most deployments. The combination that matters is the SILENT one — a node advertising nothing
/// is treated by the leader as having unlimited headroom, so falling back to "no monitor" would turn
/// the feature on and then quietly make this node the cluster's preferred placement target, which is
/// the exact opposite of what was asked for. Refuse at startup, where it can still be fixed.
/// </summary>
public class capacity_aware_assignment_requires_a_monitor
{
    [Fact]
    public async Task refuses_to_start_with_the_flag_on_and_no_monitor()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ApplicationAssembly = GetType().Assembly;
                    opts.Durability.CapacityAwareAssignment = true;
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        // The message has to name the way out, not just the problem.
        ex.Message.ShouldContain(nameof(DurabilitySettings.NodeLoadMonitor));
        ex.Message.ShouldContain(nameof(MemoryPressureLoadMonitor));
    }

    [Fact]
    public async Task starts_with_the_flag_on_and_a_monitor_supplied()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = GetType().Assembly;
                opts.Durability.CapacityAwareAssignment = true;
                opts.Durability.NodeLoadMonitor = new StubLoadMonitor(12);
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_flag_off_needs_no_monitor()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.ApplicationAssembly = GetType().Assembly)
            .StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    internal class StubLoadMonitor(double? load) : INodeLoadMonitor
    {
        public double? CurrentLoad() => load;
    }
}
