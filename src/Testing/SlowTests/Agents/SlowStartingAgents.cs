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

    public AgentStartTelemetry(int agentCount, TimeSpan startDelay)
    {
        AgentCount = agentCount;
        StartDelay = startDelay;
    }

    public int AgentCount { get; }
    public TimeSpan StartDelay { get; }

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
            await Task.Delay(StartDelay, token);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        _startedOn[uri] = nodeNumber;
        _startsPerNode.AddOrUpdate(nodeNumber, 1, (_, count) => count + 1);
    }

    internal void RecordStop(Uri uri)
    {
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Status = AgentStatus.Stopped;
        _telemetry.RecordStop(Uri);
        return Task.CompletedTask;
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
