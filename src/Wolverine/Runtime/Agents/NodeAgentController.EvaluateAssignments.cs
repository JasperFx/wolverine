using Microsoft.Extensions.Logging;
using Wolverine.Persistence;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    public AssignmentGrid? LastAssignments { get; internal set; }

    // GH-3604 / D3+D6, extended by GH-3698: leader-side, in-memory record of agent starts dispatched but not
    // yet confirmed running on their destination. A first-time assignment (grid Agent.OriginalNode == null)
    // is emitted as AssignAgent, but a heavily loaded node — e.g. one starting thousands of Marten
    // subscription agents — can take many assignment cycles to actually start each agent and persist its
    // assignment row. Until the next LoadNodeAgentState reflects it as running, the agent looks completely
    // unplaced, so without this ledger the leader re-decides its placement from scratch every evaluation:
    // re-emitting the SAME (agent -> node) AssignAgent, piling duplicate mega-batches onto the polled control
    // queue and writing ~one AssignmentChanged telemetry row per agent per cycle into the same database the
    // rebuild is already saturating — and, once the evaluation stopped waiting for the command drain,
    // dispatching the agent to a SECOND node with no stop for the copy already starting on the first.
    //
    // The ledger is projected onto the grid as Agent.PendingNode so that placement decisions and the commands
    // built from them account for the in-flight copy directly, rather than being filtered afterwards. It is
    // leader-only and in-memory: a new leader starts empty and safely re-drives everything.
    // Guards _pendingAssignments. The evaluation path is serialized by the health-check loop, but
    // GH-3750's ConfirmDispatched is called from the dispatcher's lane threads as commands resolve.
    private readonly object _pendingLock = new();

    private readonly Dictionary<Uri, PendingAssignment> _pendingAssignments = new();

    private readonly record struct PendingAssignment(Guid NodeId, DateTimeOffset SentAt);

    /// <summary>
    ///     GH-3750: refresh the pending-assignment ledger for agents a resolving batch command just
    ///     confirmed. The dispatcher's in-flight hold ends the moment the command resolves, but the
    ///     ledger entry still carries the SentAt from when the batch was DISPATCHED — for a batch of
    ///     slow starts that is minutes ago, so the TTL is instantly expired and the next evaluation
    ///     whose node-state snapshot predates the last assignment rows re-decides an agent that just
    ///     confirmed, emitting a stop at the very node that started it. Stamping confirmation time here
    ///     keeps the agent held on its node for the one snapshot cycle it takes the persisted row to
    ///     become visible, after which reconcilePendingAssignments retires the entry normally.
    /// </summary>
    internal void ConfirmDispatched(Uri[] agentUris, Guid nodeId)
    {
        if (agentUris.Length == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        lock (_pendingLock)
        {
            foreach (var uri in agentUris)
            {
                if (_pendingAssignments.TryGetValue(uri, out var pending) && pending.NodeId == nodeId)
                {
                    _pendingAssignments[uri] = new PendingAssignment(nodeId, now);
                }
            }
        }
    }

    /// <summary>
    ///     GH-3698. Asks the command dispatcher whether a start for an agent is still queued or executing.
    ///     This is what lets a pending assignment be held until the dispatch is <i>confirmed or failed</i>
    ///     instead of for a fixed TTL — <c>2 x CheckAssignmentPeriod</c> is seconds, and a wave of slow agent
    ///     starts routinely runs for a minute or more. Null on a runtime with no dispatcher (operator-driven
    ///     <c>ApplyRestrictionsAsync</c> executes its own commands inline and awaits them), in which case only
    ///     the TTL applies.
    /// </summary>
    internal delegate bool PendingDispatchProbe(Uri agentUri, out Guid nodeId);

    internal PendingDispatchProbe? PendingDispatches { get; set; }

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
        
        // GH-3987: does this evaluation trust the roster enough to rebalance? Sampled BEFORE the grid is
        // built so one consistent answer applies to the whole evaluation.
        var topologyStable = observeTopology(nodes);

        var grid = new AssignmentGrid();
        grid.OverloadShedBatchSize = Math.Max(1, _runtime.Options.Durability.OverloadShedBatchSize);

        var capabilities = nodes.SelectMany(x => x.Capabilities).Distinct().ToArray();
        grid.WithAgents(capabilities);

        var capacityAware = _runtime.Options.Durability.CapacityAwareAssignment;
        var overloadThreshold = _runtime.Options.Durability.NodeOverloadThreshold;

        foreach (var node in nodes)
        {
            var gridNode = grid.WithNode(node);

            // GH-3959: only the leader flags overload, and only when the feature is on — a node that
            // advertises no load always counts as having headroom, which keeps stores without load
            // persistence on today's behavior. The receive line sits a 10-point hysteresis band below
            // the shed line so placement and shedding never fight around one threshold.
            gridNode.IsOverloaded = capacityAware
                                    && gridNode.LoadFactor.HasValue
                                    && gridNode.LoadFactor.Value >= overloadThreshold;
            gridNode.IsAcceptingAgents = !capacityAware
                                         || !gridNode.LoadFactor.HasValue
                                         || gridNode.LoadFactor.Value < Math.Max(0, overloadThreshold - 10);
        }

        // GH-3785: the durability family places each database's agent next to that database's
        // event-subscription agents, which it can only see if those were assigned first — so it must
        // evaluate last. Everything else keeps its registration order (OrderBy is stable), which
        // previously put the durability family last only by Dictionary insertion-order accident.
        var families = _agentFamilies.Values
            .OrderBy(x => x.Scheme == PersistenceConstants.AgentScheme ? 1 : 0);

        foreach (var agentFamily in families)
        {
            try
            {
                var allAgents = await agentFamily.AllKnownAgentsAsync();
                grid.WithAgents(allAgents
                    .ToArray()); // Just in case something has gotten lost, and this is master anyway
                
                // Apply this every time to pick up any agents from above
                grid.ApplyRestrictions(restrictions);

                // GH-3698: must run AFTER restrictions (an operator pause or pin outranks a pending
                // dispatch) and BEFORE the family distributes, so the distribution balances the remaining
                // agents *around* the ones already in flight instead of spreading everything evenly and
                // then having them yanked back. See the method.
                applyPendingAssignments(grid);

                await agentFamily.EvaluateAssignmentsAsync(grid);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to reevaluate agent assignments for '{Scheme}' agents",
                    agentFamily.Scheme);
            }
        }

        // GH-3987: the stability gate. While the topology is still changing — mid rolling deploy, every
        // pod replacement re-triggers evaluation — defer pure REBALANCING moves: an agent that is running
        // somewhere and was only re-placed for evenness stays where it is until the roster has been
        // unchanged for AssignmentStabilityWindow. Everything urgent flows regardless: first-time
        // placements (OriginalNode == null), stops for detached agents, operator pins, capacity sheds,
        // duplicate healing, and moves already dispatched (PendingNode). Reverting the grid assignment —
        // rather than filtering the command afterwards — means no command is built, the pending ledger
        // never arms, and no AssignmentChanged row is written for a move that was never going to survive
        // the next topology change anyway.
        if (!topologyStable)
        {
            var deferred = 0;
            foreach (var agent in grid.AllAgents)
            {
                if (agent.OriginalNode == null || agent.AssignedNode == null) continue;
                if (ReferenceEquals(agent.AssignedNode, agent.OriginalNode)) continue;
                if (agent.PendingNode != null) continue;
                if (agent.IsPinned || agent.SheddedForCapacity) continue;

                agent.OriginalNode.Assign(agent);
                deferred++;
            }

            if (deferred > 0)
            {
                _logger.LogInformation(
                    "Deferring {Count} rebalancing move(s) until the cluster topology has been stable for {Window}",
                    deferred, _runtime.Options.Durability.AssignmentStabilityWindow);
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

        // GH-3604 / D3+D6: before the observer logs anything, so a slow start isn't punished with a
        // duplicate control-queue flood and a matching burst of AssignmentChanged telemetry rows.
        reconcilePendingAssignments(grid, commands);

        await _observer.AssignmentsChanged(grid, commands);

        LastAssignments = grid;
        LastAssignments.EvaluationTime = DateTimeOffset.UtcNow;

        if (commands.Any())
        {
            batchCommands(commands);
        }

        return commands;
    }

    // GH-3698: project the pending-assignment ledger onto the grid, so an agent whose start is already on
    // its way somewhere is a placement the distribution can SEE rather than a command to be filtered out
    // afterwards.
    //
    // Agent.OriginalNode comes from each node's PERSISTED ActiveAgents, and the assignment row only appears
    // once the agent is actually running, so a dispatched-but-unstarted agent looks completely unplaced. The
    // distribution pass therefore re-decides its placement from scratch on every evaluation and can send it
    // somewhere else. That did not matter while the leader's assignment loop was blocked behind the agent
    // command drain -- no evaluation ran during a wave at all -- but now that the two are decoupled an
    // evaluation lands every HealthCheckPollingTime while a wave of slow starts is still in flight, and the
    // re-target used to be emitted as a bare AssignAgent to the second node: the same agent running twice.
    //
    // Two things come out of this method. Agent.PendingNode, always, so TryBuildAssignmentCommand can make
    // every outcome account for the copy that may be coming up there -- a stop, or a stop-then-start, never a
    // bare start elsewhere. And, while the dispatch is still live, the placement itself: the agent is held on
    // the node we already chose.
    //
    // Runs BEFORE the family distributes precisely so the distribution sees those agents as already placed --
    // they count toward their node's share and are not in its "missing" queue, so it balances the genuinely
    // unassigned remainder around them. Pinning after the fact instead leaves the distribution's own even
    // split intact and then moves agents on top of it, which overloads whichever node the pins land on.
    //
    // Deliberately does NOT touch Agent.OriginalNode -- the ledger confirms entries by matching OriginalNode,
    // and fabricating one here would make every pending entry instantly self-confirming.
    private void applyPendingAssignments(AssignmentGrid grid)
    {
        lock (_pendingLock)
        {
            applyPendingAssignmentsLocked(grid);
        }
    }

    private void applyPendingAssignmentsLocked(AssignmentGrid grid)
    {
        if (_pendingAssignments.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // Backstop only, and it no longer decides on its own whether a dispatch is still live -- the
        // dispatcher does. This covers the window between a command completing on the wire and the
        // destination's assignment row turning up in a later LoadNodeAgentState.
        var ttl = _runtime.Options.Durability.CheckAssignmentPeriod * 2;

        var nodes = grid.Nodes.ToDictionary(x => x.NodeId);
        List<Uri>? abandoned = null;

        foreach (var agent in grid.AllAgents)
        {
            if (!_pendingAssignments.TryGetValue(agent.Uri, out var pending)) continue;

            // Observed running on the very node we dispatched to: the wait is over, not a pending placement.
            // reconcilePendingAssignments clears the ledger entry below.
            //
            // GH-3852: this used to skip EVERY agent with an OriginalNode, which silently excluded the whole
            // reassignment case -- an agent being moved is still listed in its source node's persisted
            // ActiveAgents, so OriginalNode is set and PendingNode was never populated. The ledger armed on a
            // ReassignAgent (see reconcilePendingAssignmentsLocked) but could never apply one, so the leader
            // re-decided the same move from scratch on every evaluation until the source's assignment row
            // finally disappeared. Now only a MATCHING node ends the wait.
            if (agent.OriginalNode != null && agent.OriginalNode.NodeId == pending.NodeId) continue;

            if (!nodes.TryGetValue(pending.NodeId, out var node))
            {
                // The destination has left the cluster, so there is no copy there we could reach even if one
                // did come up, and nothing to hold the agent to. Free it to be placed from scratch.
                (abandoned ??= []).Add(agent.Uri);
                continue;
            }

            agent.PendingNode = node;

            // The dispatcher holds a command from the moment it is queued until its lane is done with it,
            // whatever the outcome -- that, not the clock, is when a dispatch stops being outstanding.
            agent.PendingRetryDue = !isOutstanding(agent.Uri, pending.NodeId) && now - pending.SentAt >= ttl;

            // An operator's pause or explicit pin outranks a dispatch we have not managed to complete.
            // Re-placing a paused agent here would resurrect the GH-3663 class of bug where a pause is
            // undone by the very evaluation meant to carry it out. PendingNode is still set, so the pause
            // goes out as a stop aimed at the node that may be starting it.
            if (agent.IsPaused || agent.IsPinned) continue;

            // Gone unconfirmed for long enough: let the distribution decide afresh. Moving it is safe now
            // only because PendingNode above turns the move into a stop-then-start.
            if (agent.PendingRetryDue) continue;

            if (agent.AssignedNode?.NodeId == pending.NodeId) continue;

            node.Assign(agent);
        }

        if (abandoned != null)
        {
            foreach (var uri in abandoned) _pendingAssignments.Remove(uri);
        }
    }

    private bool isOutstanding(Uri agentUri, Guid nodeId)
    {
        var probe = PendingDispatches;
        return probe != null && probe(agentUri, out var destination) && destination == nodeId;
    }

    // Bring the pending-assignment ledger up to date with what this evaluation observed and decided. See
    // _pendingAssignments.
    private void reconcilePendingAssignments(AssignmentGrid grid, AgentCommands commands)
    {
        lock (_pendingLock)
        {
            reconcilePendingAssignmentsLocked(grid, commands);
        }
    }

    private void reconcilePendingAssignmentsLocked(AssignmentGrid grid, AgentCommands commands)
    {
        var now = DateTimeOffset.UtcNow;

        // Confirmation: an agent observed running on the very node we assigned it to is no longer pending.
        // The grid sets Agent.OriginalNode from each node's persisted ActiveAgents, so a matching
        // OriginalNode means the destination started it and persisted the assignment row since we last
        // emitted — the only question this ledger asks. Deliberately independent of what THIS evaluation
        // decides to do with the agent: GH-3663 hit the gap where the confirming evaluation was also the
        // one detaching the agent for an operator pause (AssignedNode == null), so the delivered entry was
        // never cleared and kept suppressing the post-restart AssignAgent for a full TTL.
        if (_pendingAssignments.Count > 0)
        {
            var known = new HashSet<Uri>();

            foreach (var agent in grid.AllAgents)
            {
                known.Add(agent.Uri);

                if (agent.OriginalNode != null
                    && _pendingAssignments.TryGetValue(agent.Uri, out var delivered)
                    && delivered.NodeId == agent.OriginalNode.NodeId)
                {
                    _pendingAssignments.Remove(agent.Uri);
                }
            }

            // An agent that has left its family altogether will never be confirmed and will never be
            // re-emitted, so nothing else would ever remove it. Without this the ledger only grows.
            foreach (var uri in _pendingAssignments.Keys.Where(x => !known.Contains(x)).ToArray())
            {
                _pendingAssignments.Remove(uri);
            }
        }

        // Arm from what we are about to dispatch. There is no suppression pass here any more: the grid
        // already declined to emit anything for an agent whose start is still live on its pending node.
        foreach (var command in commands)
        {
            switch (command)
            {
                case AssignAgent assign:
                    _pendingAssignments[assign.AgentUri] =
                        new PendingAssignment(assign.Destination.NodeId, now);
                    break;

                case ReassignAgent reassign:
                    // The stop half is synchronous within the command; what we are now waiting on is the
                    // start on the node taking the agent over.
                    _pendingAssignments[reassign.AgentUri] =
                        new PendingAssignment(reassign.ActiveNode.NodeId, now);
                    break;

                case StopRemoteAgent stop:
                    // A stop aimed at the very node we were waiting on ends that wait -- this is the stop
                    // TryBuildAssignmentCommand emits for a pending agent that has just been paused or
                    // detached. A stop aimed anywhere else (GH-2602 split-brain healing picks off the older
                    // of two running copies) says nothing about a dispatch still on its way.
                    if (_pendingAssignments.TryGetValue(stop.AgentUri, out var awaiting)
                        && awaiting.NodeId == stop.Destination.NodeId)
                    {
                        _pendingAssignments.Remove(stop.AgentUri);
                    }

                    break;
            }
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

        // GH-3749: reassignment was the one command type never batched, so a rebalance moving thousands of
        // agents emitted thousands of individual ReassignAgent commands -- each a serial StopAgent round
        // trip against the source inside one lane, with AgentStartBatchSize having no effect on any of them.
        // Chunked like the starts: one chunk's stops per round trip, and the confirmed remainder cascades as
        // a single AssignAgents into the destination's lane.
        foreach (var group in commands.OfType<ReassignAgent>()
                     .GroupBy(x => (x.OriginalNode, x.ActiveNode))
                     .Where(x => x.Count() > 1)
                     .ToArray())
        {
            foreach (var message in group) commands.Remove(message);

            foreach (var chunk in group.Select(x => x.AgentUri).Chunk(batchSize))
            {
                commands.Add(new ReassignAgents(group.Key.OriginalNode, group.Key.ActiveNode, chunk));
            }
        }
    }
}