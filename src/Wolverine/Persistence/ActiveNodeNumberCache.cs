using System.Runtime.CompilerServices;
using JasperFx.Core;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

/// <summary>
/// Node-wide cache of the active Wolverine node numbers, shared by every durability agent on the
/// node.
/// </summary>
/// <remarks>
/// The durability agent's recovery timer needs the active node <i>numbers</i> to spot messages
/// orphaned by a departed node, but the only persistence call that yields them —
/// <see cref="Runtime.Agents.INodeAgentPersistence.LoadAllNodesAsync" /> — also selects the entire
/// assignment table so it can populate <c>WolverineNode.ActiveAgents</c>, which this caller never
/// reads. With one durability agent per message database that turns a per-node fact into a
/// per-database query against the main store.
///
/// On a sharded deployment the cost is quadratic in the fleet: 512 databases on a five-second
/// <see cref="DurabilitySettings.ScheduledJobPollingTime" /> is ~100 calls a second, each dragging
/// back one row per assignment. Measured on a 512-database, ~10,000-agent cluster that was 76
/// calls and 772,000 rows a second, and the main store spent its time writing those result sets
/// (<c>Client:ClientWrite</c> was 164 of 170 average active sessions) while each call held a pooled
/// connection — which exhausted the pool and made the heartbeat writes time out, so the leader
/// then declared healthy nodes stale and reassigned their agents, churning the very table being
/// read.
///
/// Fetching it once per node per polling interval instead of once per database keeps the behaviour
/// (the caller already tolerates data up to one interval old, since that is how often it looks)
/// and drops the query count by the database count. Same shape, and the same reasoning, as
/// <see cref="PersistenceMetricsSweeper" /> for the metrics polling in GH-3375.
/// </remarks>
public class ActiveNodeNumberCache
{
    private static readonly ConditionalWeakTable<IWolverineRuntime, ActiveNodeNumberCache> _perRuntime = new();

    public static ActiveNodeNumberCache For(IWolverineRuntime runtime)
    {
        return _perRuntime.GetValue(runtime, r => new ActiveNodeNumberCache(r));
    }

    private readonly IWolverineRuntime _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<int>? _numbers;
    private DateTimeOffset _staleAt = DateTimeOffset.MinValue;

    internal ActiveNodeNumberCache(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// The active node numbers, fetched at most once per
    /// <see cref="DurabilitySettings.ScheduledJobPollingTime" /> for the whole node. Exceptions are
    /// deliberately not swallowed here: the caller decides what a failed lookup means, and a failure
    /// leaves the previous value in place rather than caching an empty one.
    /// </summary>
    public async ValueTask<IReadOnlyList<int>> FetchAsync(CancellationToken token)
    {
        if (fresh(out var cached)) return cached;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // a second check inside the gate: while this call waited, the database timers that
            // queued up behind it have already been served by the winner's fetch
            if (fresh(out cached)) return cached;

            var nodes = await _runtime.Storage.Nodes.LoadAllNodesAsync(token).ConfigureAwait(false);
            var numbers = nodes.Select(x => x.AssignedNodeNumber).ToList();

            _numbers = numbers;
            _staleAt = DateTimeOffset.UtcNow.Add(_runtime.DurabilitySettings.ScheduledJobPollingTime);

            return numbers;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool fresh(out IReadOnlyList<int> numbers)
    {
        var current = _numbers;
        if (current != null && DateTimeOffset.UtcNow < _staleAt)
        {
            numbers = current;
            return true;
        }

        numbers = Array.Empty<int>();
        return false;
    }
}
