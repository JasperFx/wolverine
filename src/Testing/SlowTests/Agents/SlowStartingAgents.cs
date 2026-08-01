using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Core;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace SlowTests.Agents;

/// <summary>
/// Cluster-wide instrumentation shared by every in-process node in a scale test. All three hosts run in
/// the same process, so a single shared instance gives us a true cluster-wide view of how many agent
/// starts are in flight at any instant — which is the number that exposes whether the leader's command
/// drain is actually fanning work out to more than one node at a time.
/// </summary>
public class AgentStartTelemetry
{
    private readonly ConcurrentDictionary<Uri, int> _startedOn = new();
    private readonly ConcurrentDictionary<int, int> _startsPerNode = new();
    private int _inFlight;
    private int _peakInFlight;
    private int _agentCount;

    public AgentStartTelemetry(int agentCount, TimeSpan startDelay)
    {
        _agentCount = agentCount;
        StartDelay = startDelay;
    }

    public int AgentCount => Volatile.Read(ref _agentCount);
    public TimeSpan StartDelay { get; }

    /// <summary>
    /// GH-3748/GH-3750: optional per-agent start cost, overriding <see cref="StartDelay" />. The field
    /// shape is heterogeneous — a shard behind a projection-version bump replays for minutes while its
    /// neighbors start in seconds — and re-placement churn only shows when nodes finish their chunks at
    /// different times, so a symmetric universe can hide it.
    /// </summary>
    public Func<Uri, TimeSpan>? DelayOverride { get; set; }

    /// <summary>
    /// GH-3753: how long an agent takes to let go when stopped. Zero by default. A projection version
    /// bump puts a stopping shard behind the same side-effect gate as a starting one, and #3749's lane
    /// wedge only appears when the SOURCE of a reassignment answers its stops slowly — a fast-stopping
    /// source drains thousands of reassignments without ever blocking anything. Mutable so a test can
    /// turn slow stops on for its rebalance phase and back off for teardown.
    /// </summary>
    public TimeSpan StopDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// GH-3753: expands the known agent universe mid-test. Stands in for the arrival of a new
    /// generation of agents — the projection-version-bump deploy shape, where freshly-placeable agents
    /// and reassignments of the old ones are in flight at the same time. Every node's family shares
    /// this one telemetry instance, so the whole cluster sees the new universe on its next evaluation.
    /// </summary>
    public void GrowUniverseTo(int agentCount)
    {
        if (agentCount < AgentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(agentCount), "The universe only grows");
        }

        Volatile.Write(ref _agentCount, agentCount);
    }

    /// <summary>
    /// The high-water mark of concurrent <see cref="SlowStartAgent.StartAsync" /> calls anywhere in the
    /// cluster. With N nodes each starting a chunk at MaxAgentStartParallelism, a healthy cluster should
    /// reach roughly N x MaxAgentStartParallelism. A value pinned at MaxAgentStartParallelism means only
    /// one node was ever starting agents at a time.
    /// </summary>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    public int TotalStarted => _startedOn.Count;

    /// <summary>
    /// Agent starts completed per node number, so a test can show "one node keeps growing while its peers
    /// are frozen".
    /// </summary>
    public IReadOnlyDictionary<int, int> StartsPerNode => _startsPerNode;

    public Uri[] AllAgentUris() => Enumerable.Range(0, AgentCount)
        .Select(i => new Uri($"{SlowStartAgentFamily.SchemeName}://agent/{i:0000}"))
        .ToArray();

    internal async Task RecordStartAsync(Uri uri, int nodeNumber, CancellationToken token)
    {
        var current = Interlocked.Increment(ref _inFlight);

        // Lock-free high-water mark
        int peak;
        while (current > (peak = Volatile.Read(ref _peakInFlight)))
        {
            if (Interlocked.CompareExchange(ref _peakInFlight, current, peak) == peak) break;
        }

        try
        {
            // Stand-in for a Marten subscription agent's start: a daemon shard spin-up with database
            // round-trips, made much worse by a projection version bump that gates the new version's
            // side effects behind a replay of the prior version's progression.
            await Task.Delay(DelayOverride?.Invoke(uri) ?? StartDelay, token);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        _startedOn[uri] = nodeNumber;
        _startsPerNode.AddOrUpdate(nodeNumber, 1, (_, count) => count + 1);
    }

    internal async Task RecordStopAsync(Uri uri, CancellationToken token)
    {
        var delay = StopDelay;
        if (delay > TimeSpan.Zero)
        {
            // Stand-in for a stopping daemon shard that has to finish letting go of its work — slow for
            // the same reason its start is slow during a version-bump deploy.
            await Task.Delay(delay, token);
        }

        _startedOn.TryRemove(uri, out _);
    }
}

public class SlowStartAgent : IAgent
{
    private readonly AgentStartTelemetry _telemetry;
    private readonly Func<int> _nodeNumber;

    public SlowStartAgent(Uri uri, AgentStartTelemetry telemetry, Func<int> nodeNumber)
    {
        Uri = uri;
        _telemetry = telemetry;
        _nodeNumber = nodeNumber;
    }

    public Uri Uri { get; }

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _telemetry.RecordStartAsync(Uri, _nodeNumber(), cancellationToken);
        Status = AgentStatus.Running;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _telemetry.RecordStopAsync(Uri, cancellationToken);
        Status = AgentStatus.Stopped;
    }
}

/// <summary>
/// A static agent family with a large, configurable agent universe whose members are deliberately slow to
/// start. Stands in for the customer topology in GH-3698: thousands of Marten event-subscription agents
/// across hundreds of tenant databases, each of which takes real wall-clock time to spin up.
/// </summary>
public class SlowStartAgentFamily : IStaticAgentFamily
{
    public const string SchemeName = "slowagent";

    private readonly AgentStartTelemetry _telemetry;
    private readonly WolverineOptions _options;
    private readonly LightweightCache<Uri, SlowStartAgent> _agents;

    public SlowStartAgentFamily(AgentStartTelemetry telemetry, WolverineOptions options)
    {
        _telemetry = telemetry;
        _options = options;
        _agents = new LightweightCache<Uri, SlowStartAgent>(uri =>
            new SlowStartAgent(uri, telemetry, () => _options.Durability.AssignedNodeNumber));
    }

    public string Scheme => SchemeName;

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
        => ValueTask.FromResult((IReadOnlyList<Uri>)_telemetry.AllAgentUris());

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
        => ValueTask.FromResult((IReadOnlyList<Uri>)_telemetry.AllAgentUris());

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
        => new(_agents[uri]);

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(SchemeName);
        return new ValueTask();
    }
}
