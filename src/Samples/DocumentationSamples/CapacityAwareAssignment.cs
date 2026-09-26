using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime.Agents;

namespace DocumentationSamples;

public class CapacityAwareAssignmentSamples
{
    public static async Task turn_it_on()
    {
        #region sample_capacity_aware_assignment

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql("some connection string");

                // Let the leader take each node's advertised load into account
                // when it decides where agents run
                opts.Durability.CapacityAwareAssignment = true;

                // Required! There's deliberately no default here
                opts.Durability.NodeLoadMonitor = new MemoryPressureLoadMonitor();

                // Optional, these are the defaults
                opts.Durability.NodeOverloadThreshold = 90;
                opts.Durability.OverloadShedBatchSize = 1;
            }).StartAsync();

        #endregion
    }

    public static async Task use_a_custom_monitor()
    {
        #region sample_custom_node_load_monitor

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql("some connection string");

                opts.Durability.CapacityAwareAssignment = true;
                opts.Durability.NodeLoadMonitor = new QueueDepthLoadMonitor();
            }).StartAsync();

        #endregion
    }
}

#region sample_writing_a_node_load_monitor

public class QueueDepthLoadMonitor : INodeLoadMonitor
{
    private readonly IWorkTracker _tracker;

    // Do whatever dependency injection you need in your own constructor,
    // just remember that this is a singleton
    public QueueDepthLoadMonitor(IWorkTracker tracker)
    {
        _tracker = tracker;
    }

    public QueueDepthLoadMonitor() : this(new NullWorkTracker())
    {
    }

    public double? CurrentLoad()
    {
        // This is called on every heartbeat, so it needs to be cheap and it
        // absolutely cannot block
        var depth = _tracker.CurrentDepth;

        // Return null if you genuinely have no signal right now. Careful though,
        // the leader reads "no reading" as "this node has headroom" -- so don't
        // use null as a way of saying "lightly loaded"
        if (depth < 0) return null;

        // 0-100, where 100 means "completely full"
        return Math.Clamp(100.0 * depth / _tracker.Capacity, 0, 100);
    }
}

#endregion

public interface IWorkTracker
{
    int CurrentDepth { get; }
    int Capacity { get; }
}

public class NullWorkTracker : IWorkTracker
{
    public int CurrentDepth => 0;
    public int Capacity => 1;
}
