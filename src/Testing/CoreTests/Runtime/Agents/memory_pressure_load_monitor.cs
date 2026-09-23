using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4589. The monitor's denominator has to be a real memory limit. The version this replaces divided
/// by GCMemoryInfo.TotalAvailableMemoryBytes, which on a host with no cgroup limit is the whole
/// machine's RAM -- a 116 MB service on a 128 GB box read 0.09, so the feature was inert, and silently.
/// </summary>
public class memory_pressure_load_monitor
{
    private const long OneGigabyte = 1024L * 1024 * 1024;

    [Fact]
    public void no_detectable_limit_means_no_reading_at_all()
    {
        // Explicitly null rather than "whatever this box happens to have": a reading of zero and a
        // reading of "I cannot measure this" have to stay distinguishable, because the leader treats
        // null as "still eligible, load unknown" and a low number as "place here first".
        new MemoryPressureLoadMonitor(null).CurrentLoad().ShouldBeNull();
    }

    [Fact]
    public void an_unlimited_sentinel_is_not_a_limit()
    {
        // The kernel reports "no limit" as a sentinel near the top of the range rather than by
        // omitting the file, and cgroup v1's exact value varies with page size.
        new MemoryPressureLoadMonitor(long.MaxValue).CurrentLoad().ShouldBeNull();
        new MemoryPressureLoadMonitor(0x7FFFFFFFFFFFF000).CurrentLoad().ShouldBeNull();
    }

    [Fact]
    public void a_zero_or_negative_limit_is_not_a_limit()
    {
        new MemoryPressureLoadMonitor(0).CurrentLoad().ShouldBeNull();
        new MemoryPressureLoadMonitor(-1).CurrentLoad().ShouldBeNull();
    }

    [Fact]
    public void reads_as_a_percentage_of_the_supplied_limit()
    {
        new StubbedWorkingSet(OneGigabyte, OneGigabyte / 4).CurrentLoad().ShouldBe(25);
        new StubbedWorkingSet(OneGigabyte, OneGigabyte / 2).CurrentLoad().ShouldBe(50);
    }

    [Fact]
    public void the_denominator_is_the_limit_and_not_the_machine()
    {
        // The regression in one assertion: the same process reads TEN TIMES busier against a limit a
        // tenth the size. The formula this replaces divided by the machine's RAM, so on an
        // unconstrained host every reading collapsed toward zero no matter what the process was doing.
        var constrained = new MemoryPressureLoadMonitor(Environment.WorkingSet * 2).CurrentLoad();
        var roomy = new MemoryPressureLoadMonitor(Environment.WorkingSet * 20).CurrentLoad();

        constrained.ShouldNotBeNull();
        roomy.ShouldNotBeNull();

        constrained.Value.ShouldBeGreaterThan(roomy.Value * 5);
    }

    [Fact]
    public void a_limit_far_below_the_working_set_clamps_at_100()
    {
        new MemoryPressureLoadMonitor(1024).CurrentLoad().ShouldBe(100);
    }

    [Fact]
    public void a_rise_is_taken_whole()
    {
        var monitor = new StubbedWorkingSet(OneGigabyte, OneGigabyte / 10);
        monitor.CurrentLoad().ShouldBe(10);

        // Climbing pressure is news the leader needs on THIS heartbeat, not three heartbeats from now.
        monitor.WorkingSet = OneGigabyte * 8 / 10;
        monitor.CurrentLoad().ShouldBe(80);
    }

    [Fact]
    public void a_fall_decays_instead_of_arriving_whole()
    {
        var monitor = new StubbedWorkingSet(OneGigabyte, OneGigabyte * 8 / 10);
        monitor.CurrentLoad().ShouldBe(80);

        // One lucky GC is not evidence the node is out of trouble: 80 -> 0 arrives as 56, not 0.
        monitor.WorkingSet = 0;
        monitor.CurrentLoad().ShouldBe(56);
        monitor.CurrentLoad().ShouldBe(39.2);

        // ...but it does keep falling, so a node that genuinely recovered becomes eligible again.
        for (var i = 0; i < 20; i++) monitor.CurrentLoad();
        monitor.CurrentLoad().ShouldNotBeNull().ShouldBeLessThan(1);
    }

    [Fact]
    public void the_detected_limit_is_reported_so_a_null_reading_can_be_explained()
    {
        // Whatever this box is, Limit and CurrentLoad have to agree about whether there is a signal --
        // otherwise "advertising nothing" is indistinguishable from a bug.
        var monitor = new MemoryPressureLoadMonitor();

        if (monitor.Limit == null)
        {
            monitor.CurrentLoad().ShouldBeNull();
        }
        else
        {
            monitor.Limit.Value.ShouldBeGreaterThan(0);
            monitor.CurrentLoad().ShouldNotBeNull();
        }
    }

    /// <summary>
    /// Drives the arithmetic from a fixed RSS. Reading the real Environment.WorkingSet mid-suite gives
    /// a value that moves between the call and the assertion, and the monitor smooths across calls, so
    /// a live comparison is inherently racy — it passed alone and failed inside the full suite.
    /// </summary>
    internal class StubbedWorkingSet(long? limit, long workingSet) : MemoryPressureLoadMonitor(limit)
    {
        public long WorkingSet { get; set; } = workingSet;

        protected override long CurrentWorkingSet => WorkingSet;
    }
}
