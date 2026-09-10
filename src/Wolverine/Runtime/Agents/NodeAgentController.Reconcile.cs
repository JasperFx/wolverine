using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    // GH-3987: consecutive-tick observation streaks for local assignment discrepancies, keyed by agent
    // Uri. Only touched from the serialized health-check path. An entry is removed the moment the
    // discrepancy is no longer observed, so only a SUSTAINED mismatch is ever acted on — anything
    // legitimately in flight (a start racing its assignment row, a stop racing its row delete) clears
    // itself within a tick.
    private readonly Dictionary<Uri, int> _reconcileObservations = new();

    /// <summary>
    ///     GH-3987: the node-side assigned-vs-running reconciliation sweep. Runs on every node on every
    ///     health-check tick, independently of leadership. Compares what the durable assignment table
    ///     says this node owns against what is actually registered here, and heals both directions of
    ///     divergence the leader structurally cannot see:
    ///     <list type="bullet">
    ///         <item>an agent whose row says this node owns it but which is not registered here at all —
    ///         the "assigned but not running" wedge — is started;</item>
    ///         <item>an agent running here that no durable row accounts for is stopped if another live
    ///         node owns the durable claim (this copy is split-brain residue the GH-2602 healer cannot
    ///         see, since it only compares durable rows), or re-claimed by restoring this node's row if
    ///         nobody owns it.</item>
    ///     </list>
    ///     The existing local sweep (<see cref="ReportFailedLocalAgentsAsync" />) covers agents that are
    ///     registered but wedged/paused; this one covers registration-vs-table divergence.
    /// </summary>
    internal async Task ReconcileLocalAgentsAsync(IReadOnlyList<WolverineNode> nodes, AgentRestrictions restrictions)
    {
        var threshold = _runtime.Options.Durability.LocalAgentReconciliationThreshold;
        if (threshold <= 0)
        {
            return;
        }

        var self = nodes.FirstOrDefault(x => x.NodeId == _runtime.Options.UniqueNodeId);
        if (self == null)
        {
            // Snapshot lag; there is nothing trustworthy to compare against on this tick
            return;
        }

        var claimed = self.ActiveAgents.Where(x => x != LeaderUri).ToHashSet();
        var running = Agents.Keys.Where(x => x != LeaderUri).ToHashSet();
        var paused = restrictions.FindPausedAgentUris().ToHashSet();

        var mismatches = new List<(Uri Uri, bool RunningNotClaimed)>();

        foreach (var uri in running)
        {
            if (claimed.Contains(uri)) continue;
            if (_stoppingAgents.ContainsKey(uri)) continue;

            mismatches.Add((uri, true));
        }

        foreach (var uri in claimed)
        {
            if (running.Contains(uri)) continue;
            if (_stoppingAgents.ContainsKey(uri)) continue;

            // An agent this node released for failing here must not be dragged back by its own stale
            // row, an operator's pause outranks the row, and a row for a scheme this deployment cannot
            // run (blue/green) is a peer's business.
            if (_releasedAgents.ContainsKey(uri)) continue;
            if (paused.Contains(uri)) continue;
            if (!_agentFamilies.ContainsKey(uri.Scheme)) continue;

            mismatches.Add((uri, false));
        }

        // Streak accounting: reset anything that healed on its own, then only act on discrepancies
        // observed for `threshold` consecutive ticks.
        var current = mismatches.Select(x => x.Uri).ToHashSet();
        foreach (var recovered in _reconcileObservations.Keys.Where(x => !current.Contains(x)).ToArray())
        {
            _reconcileObservations.Remove(recovered);
        }

        foreach (var (uri, runningNotClaimed) in mismatches)
        {
            var count = (_reconcileObservations.TryGetValue(uri, out var previous) ? previous : 0) + 1;
            _reconcileObservations[uri] = count;

            if (count < threshold)
            {
                continue;
            }

            _reconcileObservations.Remove(uri);

            try
            {
                if (runningNotClaimed)
                {
                    var owner = nodes.FirstOrDefault(x =>
                        x.NodeId != self.NodeId && x.ActiveAgents.Contains(uri));

                    if (owner != null)
                    {
                        _logger.LogWarning(
                            "Agent {AgentUri} is running on node {NodeNumber} but its durable assignment belongs to node {OwnerNodeNumber}; stopping the local copy",
                            uri, _runtime.Options.Durability.AssignedNodeNumber, owner.AssignedNodeNumber);

                        await StopAgentAsync(uri);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Agent {AgentUri} is running on node {NodeNumber} with no durable assignment row anywhere; restoring this node's claim",
                            uri, _runtime.Options.Durability.AssignedNodeNumber);

                        await upsertAssignmentAsync(uri);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Agent {AgentUri} is durably assigned to node {NodeNumber} but is not running here; starting it",
                        uri, _runtime.Options.Durability.AssignedNodeNumber);

                    await StartAgentAsync(uri);
                }
            }
            catch (Exception e)
            {
                // One agent's failure must not take down the sweep; a failed start is already counted
                // and escalated by the _failedStarts / release machinery.
                _logger.LogError(e, "Error reconciling agent {AgentUri} on node {NodeNumber}", uri,
                    _runtime.Options.Durability.AssignedNodeNumber);
            }
        }
    }
}
