using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    public AssignmentGrid? LastAssignments { get; internal set; }

    // GH-3604 / D3+D6: leader-side, in-memory record of AssignAgent commands emitted but not yet confirmed
    // running on their destination. A first-time assignment (grid Agent.OriginalNode == null) is emitted as
    // AssignAgent, but a heavily loaded node — e.g. one starting thousands of Marten subscription agents —
    // can take many assignment cycles to actually start each agent and persist its assignment row. Until the
    // next LoadNodeAgentState reflects it as running, the leader would re-emit the SAME (agent -> node)
    // AssignAgent every ~CheckAssignmentPeriod, piling duplicate mega-batches onto the polled control queue
    // and writing ~one AssignmentChanged telemetry row per agent per cycle into the same database the
    // rebuild is already saturating. This ledger suppresses those re-emissions for a TTL. It is leader-only
    // and in-memory: a new leader starts empty and safely re-emits.
    private readonly Dictionary<Uri, PendingAssignment> _pendingAssignments = new();

    private readonly record struct PendingAssignment(Guid NodeId, DateTimeOffset SentAt);

    // Tested w/ integration tests all the way
    public async Task<AgentCommands> EvaluateAssignmentsAsync(
        IReadOnlyList<WolverineNode> nodes,
        AgentRestrictions restrictions)
    {
        using var activity = ShouldTraceHealthCheck() 
            ? WolverineTracing.ActivitySource.StartActivity("wolverine_node_assignments") 
            : null;

        // Not sure how this *could* happen, but we had a report of it happening in production
        // probably because someone messed w/ the database though
        if (!nodes.Any())
        {
            // At least use the current node
            nodes = new List<WolverineNode> { WolverineNode.For(_runtime.Options) };
            nodes[0].AssignAgents([LeaderUri]);
        }
        else if (nodes.All(x => x.NodeId != _runtime.Options.UniqueNodeId))
        {
            // GH-2682 defense in depth: if the caller hands us a non-empty
            // node list that's missing the current node (e.g. a stale snapshot
            // read filtered self out), inject self so the assignment grid
            // doesn't omit the leader. The current node only owes the LeaderUri
            // here when IsLeader is true on this tick — the heartbeat path
            // upstream is responsible for actually calling AddAssignmentAsync.
            var self = WolverineNode.For(_runtime.Options);
            if (IsLeader) self.AssignAgents([LeaderUri]);
            nodes = nodes.Concat(new[] { self }).ToList();
        }
        
        var grid = new AssignmentGrid();

        var capabilities = nodes.SelectMany(x => x.Capabilities).Distinct().ToArray();
        grid.WithAgents(capabilities);
        
        foreach (var node in nodes)
        {
            grid.WithNode(node);
        }

        foreach (var agentFamily in _agentFamilies.Values)
        {
            try
            {
                var allAgents = await agentFamily.AllKnownAgentsAsync();
                grid.WithAgents(allAgents
                    .ToArray()); // Just in case something has gotten lost, and this is master anyway
                
                // Apply this every time to pick up any agents from above
                grid.ApplyRestrictions(restrictions);
                
                await agentFamily.EvaluateAssignmentsAsync(grid);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to reevaluate agent assignments for '{Scheme}' agents",
                    agentFamily.Scheme);
            }
        }

        var commands = new AgentCommands();

        foreach (var agent in grid.AllAgents)
        {
            if (agent.TryBuildAssignmentCommand(out var agentCommand))
            {
                commands.Add(agentCommand);
            }
        }

        // Heal split-brain residue: if any agent is reported running on more
        // than one node — most likely because a stale leader and the current
        // leader both dispatched AssignAgent for the same agent in close
        // succession before the stale leader stepped down — emit a
        // StopRemoteAgent for the older copy. The freshest copy stays
        // running; the next assignment cycle will rebalance if needed. See
        // GH-2602.
        foreach (var report in grid.DuplicateAgentReports)
        {
            _logger.LogWarning(
                "Detected duplicate agent {AgentUri} reported running on Node {ExistingNode} and Node {NewNode} — sending StopRemoteAgent to the older copy to heal split-brain residue.",
                report.AgentUri, report.ExistingNode.AssignedId, report.NewNode.AssignedId);

            commands.Add(new StopRemoteAgent(report.AgentUri, report.ExistingNode.ToDestination()));
        }

        // GH-3604 / D3+D6: drop AssignAgent commands still in flight from a recent wave BEFORE the observer
        // logs them, so a slow start isn't punished with a duplicate control-queue flood and a matching
        // burst of AssignmentChanged telemetry rows.
        suppressPendingAssignments(grid, commands);

        await _observer.AssignmentsChanged(grid, commands);

        LastAssignments = grid;
        LastAssignments.EvaluationTime = DateTimeOffset.UtcNow;

        if (commands.Any())
        {
            batchCommands(commands);
        }

        return commands;
    }

    // Confirm-and-suppress the pending-assignment ledger for this evaluation. See _pendingAssignments.
    private void suppressPendingAssignments(AssignmentGrid grid, AgentCommands commands)
    {
        var now = DateTimeOffset.UtcNow;

        // TTL must exceed the worst-case time for a destination to actually start an assigned agent and have
        // it show up in a later LoadNodeAgentState. 2x CheckAssignmentPeriod is the conservative default; a
        // genuinely stuck start is retried once the entry expires.
        var ttl = _runtime.Options.Durability.CheckAssignmentPeriod * 2;

        // Confirmation: an agent now running on the very node we assigned it to is no longer pending. The
        // grid sets Agent.OriginalNode from each node's persisted ActiveAgents, so OriginalNode == AssignedNode
        // means the destination started it and persisted the assignment row since we last emitted.
        foreach (var agent in grid.AllAgents)
        {
            if (agent.OriginalNode != null && ReferenceEquals(agent.AssignedNode, agent.OriginalNode))
            {
                _pendingAssignments.Remove(agent.Uri);
            }
        }

        // Expire stale entries so a start that never took is eventually re-driven.
        if (_pendingAssignments.Count > 0)
        {
            var expired = _pendingAssignments
                .Where(kv => now - kv.Value.SentAt >= ttl)
                .Select(kv => kv.Key)
                .ToArray();
            foreach (var uri in expired)
            {
                _pendingAssignments.Remove(uri);
            }
        }

        var suppressed = 0;
        for (var i = commands.Count - 1; i >= 0; i--)
        {
            if (commands[i] is not AssignAgent assign)
            {
                continue;
            }

            if (_pendingAssignments.TryGetValue(assign.AgentUri, out var pending)
                && pending.NodeId == assign.Destination.NodeId)
            {
                // Same (agent -> node) assignment still in flight from a recent wave: don't re-emit it.
                commands.RemoveAt(i);
                suppressed++;
            }
            else
            {
                // New assignment, a re-target to a different node, or a TTL-expired retry: emit it and (re)arm
                // the ledger entry.
                _pendingAssignments[assign.AgentUri] = new PendingAssignment(assign.Destination.NodeId, now);
            }
        }

        if (suppressed > 0)
        {
            _logger.LogDebug(
                "Suppressed {Count} AssignAgent command(s) still in flight from a recent assignment wave",
                suppressed);
        }
    }

    private void batchCommands(List<IAgentCommand> commands)
    {
        // GH-3604 / D3: chunk a destination's assignments into batches of at most AgentStartBatchSize rather
        // than one mega-batch. A node cannot start thousands of daemon agents inside one reply window; with
        // WO-5's pending-assignment ledger, one chunk in flight per destination at a time is enough.
        var batchSize = Math.Max(1, _runtime.Options.Durability.AgentStartBatchSize);

        foreach (var group in commands.OfType<AssignAgent>().GroupBy(x => x.Destination).Where(x => x.Count() > 1).ToArray())
        {
            foreach (var message in group) commands.Remove(message);

            foreach (var chunk in group.Select(x => x.AgentUri).Chunk(batchSize))
            {
                commands.Add(new AssignAgents(group.Key, chunk));
            }
        }

        foreach (var group in commands.OfType<StopRemoteAgent>().GroupBy(x => x.Destination).Where(x => x.Count() > 1)
                     .ToArray())
        {
            var stopAgents = new StopRemoteAgents(group.Key, group.Select(x => x.AgentUri).ToArray());

            foreach (var message in group) commands.Remove(message);

            commands.Add(stopAgents);
        }
    }
}