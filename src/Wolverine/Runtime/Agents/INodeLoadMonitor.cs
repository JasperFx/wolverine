namespace Wolverine.Runtime.Agents;

/// <summary>
///     Samples this node's load for capacity-aware agent assignment
///     (<see cref="DurabilitySettings.CapacityAwareAssignment" />). The value is advertised to the
///     cluster on every heartbeat.
/// </summary>
public interface INodeLoadMonitor
{
    /// <summary>
    ///     The node's current load as a percentage (0–100), or null when there is no usable signal.
    ///     Called from the heartbeat path, so implementations must be cheap and non-blocking.
    /// </summary>
    double? CurrentLoad();
}

/// <summary>
///     Default <see cref="INodeLoadMonitor" />: this process's resident memory
///     (<see cref="Environment.WorkingSet" />) as a percentage of the GC's memory budget, so 100 is
///     roughly where managed allocations start failing. A rising reading is taken immediately; a
///     falling one decays gradually, so one lucky GC can't mask sustained pressure.
/// </summary>
public class MemoryPressureLoadMonitor : INodeLoadMonitor
{
    private double _smoothed;

    public double? CurrentLoad()
    {
        var info = GC.GetGCMemoryInfo();
        if (info.TotalAvailableMemoryBytes <= 0)
        {
            return null;
        }

        // Deliberately WorkingSet against the GC budget (75% of the cgroup limit in a container),
        // NOT GCMemoryInfo.MemoryLoadBytes: inside a cgroup the latter includes page cache and other
        // processes, reads above 100% on a healthy node, and barely falls when agents stop.
        // WorkingSet can exceed the budget (native memory, retained segments), so clamp to the
        // documented 0-100 range before smoothing -- beyond 100 there is no more information, and
        // clamping first keeps the decay from starting at a phantom value.
        var raw = Math.Clamp(100.0 * Environment.WorkingSet / info.TotalAvailableMemoryBytes, 0, 100);

        _smoothed = raw >= _smoothed
            ? raw
            : _smoothed * 0.7 + raw * 0.3;

        return Math.Round(_smoothed, 2);
    }
}
