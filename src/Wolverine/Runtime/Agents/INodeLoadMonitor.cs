namespace Wolverine.Runtime.Agents;

/// <summary>
///     Samples this node's load for capacity-aware agent assignment
///     (<see cref="DurabilitySettings.CapacityAwareAssignment" />). The value is advertised to the
///     cluster on every heartbeat.
/// </summary>
/// <remarks>
///     There is deliberately no default implementation. What "load" means is application-specific —
///     memory for a node running thousands of async daemon shards, queue depth or handler latency for
///     a node doing ordinary message work — and a built-in default would be a guess that looks
///     authoritative while being wrong for most deployments. Enabling
///     <see cref="DurabilitySettings.CapacityAwareAssignment" /> without supplying one is a startup
///     error rather than a silent fallback. <see cref="MemoryPressureLoadMonitor" /> is available for
///     the memory case, opt-in.
/// </remarks>
public interface INodeLoadMonitor
{
    /// <summary>
    ///     The node's current load as a percentage (0–100), or null when there is no usable signal.
    ///     Called from the heartbeat path, so implementations must be cheap and non-blocking.
    /// </summary>
    /// <remarks>
    ///     Returning null is not neutral: the leader treats a node advertising nothing as having
    ///     headroom, so it stays eligible for placement. Return null when the signal is genuinely
    ///     unavailable, not as a way of saying "lightly loaded".
    /// </remarks>
    double? CurrentLoad();
}

/// <summary>
///     An opt-in <see cref="INodeLoadMonitor" /> reporting this process's resident memory
///     (<see cref="Environment.WorkingSet" />) as a percentage of the memory limit the process is
///     actually running under. A rising reading is taken immediately; a falling one decays gradually,
///     so one lucky GC can't mask sustained pressure.
/// </summary>
/// <remarks>
///     <para>
///         <b>Requires a memory limit to exist.</b> With no cgroup limit and no configured GC heap
///         hard limit — a bare VM, a container run without <c>--memory</c> — there is no ceiling to
///         measure against, and this returns null rather than inventing a denominator. An earlier
///         version divided by <c>GCMemoryInfo.TotalAvailableMemoryBytes</c>, which on an
///         unconstrained host is the whole machine's RAM: a 116 MB service on a 128 GB box read 0.09,
///         so the feature was inert and silently so.
///     </para>
///     <para>
///         The numerator is full process RSS, not managed heap, because that is what a cgroup limit
///         actually kills on. That does mean the reading includes native allocations and retained
///         segments the GC cannot release, which is the intent: they consume the same budget.
///     </para>
/// </remarks>
public class MemoryPressureLoadMonitor : INodeLoadMonitor
{
    // The kernel reports "no limit" as a sentinel near the top of the range rather than as an absent
    // file. Anything at or above this is treated as unlimited: no real machine has a petabyte of RAM,
    // and cgroup v1's actual sentinel varies by page size, so an exact comparison would miss.
    private const long UnlimitedSentinel = 1L << 50;

    private readonly long? _limit;
    private double _smoothed;

    public MemoryPressureLoadMonitor() : this(DetectMemoryLimit())
    {
    }

    /// <summary>
    ///     Supply the memory limit in bytes explicitly, for a deployment where the limit is known to
    ///     the application but not visible to the process (or to test the arithmetic).
    /// </summary>
    public MemoryPressureLoadMonitor(long? limitInBytes)
    {
        _limit = limitInBytes is > 0 and < UnlimitedSentinel ? limitInBytes : null;
    }

    /// <summary>
    ///     The memory limit this monitor is measuring against, or null when none was detected — in
    ///     which case <see cref="CurrentLoad" /> always returns null.
    /// </summary>
    public long? Limit => _limit;

    /// <summary>
    ///     This process's resident set size. Overridable so the smoothing and the arithmetic can be
    ///     driven deterministically from a test; production always reads
    ///     <see cref="Environment.WorkingSet" />.
    /// </summary>
    protected virtual long CurrentWorkingSet => Environment.WorkingSet;

    public double? CurrentLoad()
    {
        if (_limit == null)
        {
            return null;
        }

        // Clamp before smoothing: RSS can exceed the limit briefly before the kernel reacts, and
        // beyond 100 there is no more information to carry. Clamping first also keeps the decay from
        // starting at a phantom value.
        var raw = Math.Clamp(100.0 * CurrentWorkingSet / _limit.Value, 0, 100);

        // A rise is taken whole and a fall decays: memory pressure that is climbing is news the leader
        // needs this heartbeat, while a single lucky GC is not evidence the node is out of trouble.
        _smoothed = raw >= _smoothed
            ? raw
            : _smoothed * 0.7 + raw * 0.3;

        return Math.Round(_smoothed, 2);
    }

    /// <summary>
    ///     The memory limit this process runs under, or null when it is unconstrained. Reads the
    ///     cgroup limit on Linux and falls back to a configured GC heap hard limit.
    /// </summary>
    public static long? DetectMemoryLimit()
    {
        // cgroup v2 first: it is what every current container runtime uses, and a v2 host may still
        // carry a vestigial v1 hierarchy that reports the machine's memory as the "limit".
        foreach (var path in new[]
                 {
                     "/sys/fs/cgroup/memory.max",                    // v2, unified
                     "/sys/fs/cgroup/memory/memory.limit_in_bytes"   // v1
                 })
        {
            var limit = readCgroupLimit(path);
            if (limit != null)
            {
                return limit;
            }
        }

        // No cgroup, but the GC may still have been given an explicit hard limit through
        // runtimeconfig. When that is set, TotalAvailableMemoryBytes IS the ceiling rather than the
        // machine's RAM, and it is a real limit worth measuring against.
        var configured = AppContext.GetData("GCHeapHardLimit") ?? AppContext.GetData("System.GC.HeapHardLimit");
        if (configured != null)
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes is > 0 and < UnlimitedSentinel)
            {
                return info.TotalAvailableMemoryBytes;
            }
        }

        return null;
    }

    private static long? readCgroupLimit(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();

            // cgroup v2 spells "no limit" as the literal "max".
            if (text.Length == 0 || text == "max")
            {
                return null;
            }

            return long.TryParse(text, out var value) && value is > 0 and < UnlimitedSentinel
                ? value
                : null;
        }
        catch (Exception)
        {
            // An unreadable cgroup file is not a reason to fail a heartbeat -- it just means this
            // monitor has no signal, which CurrentLoad already reports honestly as null.
            return null;
        }
    }
}
