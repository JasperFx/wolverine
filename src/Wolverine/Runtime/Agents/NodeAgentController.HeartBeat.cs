using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public record CheckAgentHealth : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return runtime.Agents.DoHealthChecksAsync();
    }

    public byte[] Write()
    {
        return [];
    }

    public static object Read(byte[] bytes)
    {
        return new CheckAgentHealth();
    }
}

public partial class NodeAgentController
{
    public bool IsLeader { get; private set; }

    public async Task<AgentCommands> DoHealthChecksAsync()
    {
        if (_cancellation.IsCancellationRequested)
            return AgentCommands.Empty;

        // Re-entrancy guard: the heartbeat loop and a CheckAgentHealth
        // message handler can call DoHealthChecksAsync concurrently.
        // Without this, two callers race on shared mutable state in
        // TryAttainLeadershipLockAsync (_lastLockIndex / _lastLockETag),
        // causing one renewal to fail and triggering a spurious stepdown.
        // Non-blocking: if a health check is already in flight, skip.
        if (Interlocked.CompareExchange(ref _healthCheckGuard, 1, 0) != 0)
            return AgentCommands.Empty;

        try
        {
            return await DoHealthChecksInternalAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _healthCheckGuard, 0);
        }
    }

    // Build a full picture of THIS node's identity as the process currently knows it: the assigned node
    // number, the capabilities captured at startup, and the agents registered on it right now. Used both to
    // refresh the heartbeat and, on a miss, to re-register the node with its real identity.
    private WolverineNode buildLocalNode()
    {
        var node = WolverineNode.For(_runtime.Options);
        node.AssignedNodeNumber = _runtime.Options.Durability.AssignedNodeNumber;
        node.Capabilities.AddRange(_capabilities);
        node.AssignAgents(Agents.Keys.ToArray());
        return node;
    }

    // GH-3604 / D2: refresh this node's heartbeat, and if its row is gone -- a peer deleted it out from
    // under a still-live node under churn -- re-register with the REAL identity (same node number, captured
    // capabilities) and restore its agent assignment rows, instead of letting each store blindly insert a
    // skeleton row (fresh number, no capabilities) that drops the node out of capability-matched
    // distribution and makes the grid re-issue its whole agent universe forever.
    private async Task ensureLocalNodeRegisteredAsync(CancellationToken token)
    {
        // Hot path: MarkHealthCheckAsync only needs the node id (it is an UPDATE by id), so pass the cheap
        // skeleton and only build the full identity picture (node number, capabilities, every running agent)
        // on the rare miss. buildLocalNode enumerates every agent on the node, so calling it every heartbeat
        // would be a real cost on the thousands-of-agents nodes this whole fix is about.
        var existed = await _persistence.MarkHealthCheckAsync(WolverineNode.For(_runtime.Options), token);
        if (existed)
        {
            return;
        }

        var node = buildLocalNode();
        await _persistence.ReregisterNodeAsync(node, token);

        var agentUris = node.ActiveAgents.ToArray();
        if (agentUris.Length > 0)
        {
            await _persistence.AssignAgentsAsync(_runtime.Options.UniqueNodeId, agentUris, token);
        }

        _logger.LogWarning(
            "Node {NodeNumber} re-registered its row after it was deleted out from under a still-live node (peer ejection); restored {Count} agent assignment(s)",
            node.AssignedNodeNumber, agentUris.Length);
    }

    private async Task<AgentCommands> DoHealthChecksInternalAsync()
    {
        using var activity = ShouldTraceHealthCheck()
            ? WolverineTracing.ActivitySource.StartActivity("wolverine_node_assignments")
            : null;

        // Write the health check regardless, and due to GH-1232 do an upsert. GH-3604 / D2: if our row was
        // deleted out from under us by a peer, re-register with our real identity here rather than letting
        // the store insert a capability-less skeleton with a new node number.
        await ensureLocalNodeRegisteredAsync(_cancellation.Token);

        var (nodes, restrictions) = await _persistence.LoadNodeAgentStateAsync(_cancellation.Token);


        // Check for stale nodes that are no longer writing health checks. By
        // definition we just wrote our own heartbeat above, so we must never
        // consider ourselves stale on this tick — a stale snapshot read (read
        // replica lag, snapshot isolation, GC pause between the write and the
        // read, Oracle session-TZ round-trip, an aggressive StaleNodeTimeout)
        // could otherwise fold us into staleNodes. That path crashes
        // tryStartLeadershipAsync with NRE on `self!.AssignAgents([LeaderUri])`
        // *after* IsLeader=true, the lock is held, AssumedLeadership has
        // fired, and the assignment row is written — leaving the cluster in a
        // half-elected state with no agent dispatch. See GH-2682.
        var selfNodeId = _runtime.Options.UniqueNodeId;
        var staleTime = DateTimeOffset.UtcNow.Subtract(_runtime.Options.Durability.StaleNodeTimeout);
        var staleNodes = nodes
            .Where(x => x.LastHealthCheck < staleTime && x.NodeId != selfNodeId)
            .ToArray();
        nodes = nodes.Where(x => !staleNodes.Contains(x)).ToList();

        // Defensive: if the snapshot didn't include our own row at all
        // (read-after-write lag against the upsert above, brand-new node still
        // propagating), inject self so downstream leader-election and
        // assignment-evaluation code can find us. We rely on the next tick to
        // pick up the persisted row with its full Capabilities / ActiveAgents.
        if (nodes.All(x => x.NodeId != selfNodeId))
        {
            nodes = nodes.Concat(new[] { WolverineNode.For(_runtime.Options) }).ToList();
        }

        // Do it no matter what
        await ejectStaleNodes(staleNodes);

        // Detect lost leadership: we *thought* we were the leader (from a
        // previous heartbeat tick) but our underlying advisory lock has been
        // released server-side. With the AdvisoryLock.HasLock liveness ping
        // (Layer 1) this branch is reachable; without it, the bug from
        // GH-2602 manifests as two nodes simultaneously claiming leadership.
        // Step down cleanly here, then fall through to the normal election
        // path so this same tick either re-attains the lock (if no one else
        // has taken it) or peacefully becomes a follower.
        if (IsLeader && !_persistence.HasLeadershipLock())
        {
            await stepDownAsync("the leadership advisory lock was released server-side");
        }

        // Always call TryAttainLeadershipLockAsync. If we're already leader it
        // refreshes the lease so lease-based backends (RavenDb, Cosmos) don't
        // age out and trigger a false stepdown; otherwise it's the normal
        // election attempt.
        try
        {
            if (await _persistence.TryAttainLeadershipLockAsync(_cancellation.Token))
            {
                if (IsLeader)
                {
                    return await EvaluateAssignmentsAsync(nodes, restrictions);
                }

                return await tryStartLeadershipAsync(nodes, restrictions);
            }

            if (IsLeader)
            {
                await stepDownAsync("the leadership advisory lock could not be renewed");
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to attain a leadership lock");
        }

        return AgentCommands.Empty;
    }

    /// <summary>
    /// Reverse the local effects of <see cref="tryStartLeadershipAsync"/> when
    /// the node discovers it is no longer the leader server-side. Stops the
    /// local <see cref="NodeAgentController.LeaderUri"/> agent so this node
    /// stops dispatching <c>AssignAgent</c> / <c>ReassignAgent</c> commands,
    /// best-effort releases the persistence-layer leadership lock, and
    /// notifies observers. The caller falls through to the normal election
    /// path so a fresh leadership election happens on the same tick. See
    /// GH-2602.
    /// </summary>
    internal async Task stepDownAsync(string reason)
    {
        _logger.LogWarning(
            "Node {NodeNumber} stepping down from leadership: {Reason}. Triggering a new leadership election.",
            _runtime.Options.Durability.AssignedNodeNumber, reason);

        IsLeader = false;

        try
        {
            if (Agents.ContainsKey(LeaderUri))
            {
                await StopAgentAsync(LeaderUri);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error stopping the local leader agent during leadership step-down on node {NodeNumber}",
                _runtime.Options.Durability.AssignedNodeNumber);
        }

        try
        {
            await _persistence.ReleaseLeadershipLockAsync();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e,
                "Error trying to release the leadership lock during step-down (non-fatal)");
        }

        try
        {
            await _observer.LostLeadership();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e,
                "Observer.LostLeadership threw during step-down (non-fatal)");
        }
    }

    private async Task<AgentCommands> tryStartLeadershipAsync(IReadOnlyList<WolverineNode> nodes,
        AgentRestrictions restrictions)
    {
        try
        {
            await ensureLocalNodeRegisteredAsync(_cancellation.Token);
            // If this fails, release the leadership lock!
            await _persistence.AddAssignmentAsync(_runtime.Options.UniqueNodeId, LeaderUri,
                _cancellation.Token);
        }
        catch (Exception)
        {
            await _persistence.ReleaseLeadershipLockAsync();
            throw;
        }

        IsLeader = true;

        _logger.LogInformation("Node {NodeNumber} successfully assumed leadership", _runtime.Options.UniqueNodeId);

        await _observer.AssumedLeadership();

        // This is important, some of the assignment logic depends on knowing what the leader is
        var self = nodes.FirstOrDefault(x => x.NodeId == _runtime.Options.UniqueNodeId);
        self!.AssignAgents([LeaderUri]);
        
        return await EvaluateAssignmentsAsync(nodes, restrictions);
    }

    private async Task ejectStaleNodes(IReadOnlyList<WolverineNode> staleNodes)
    {
        // As per GH-1116, don't delete yourself!
        foreach (var staleNode in staleNodes.Where(x => x.AssignedNodeNumber != _runtime.DurabilitySettings.AssignedNodeNumber))
        {
            await _persistence.DeleteAsync(staleNode.NodeId, staleNode.AssignedNodeNumber);
        }

        if (staleNodes.Any())
        {
            await _observer.StaleNodes(staleNodes);
        }
    }

    public void CancelHeartbeatChecking()
    {
        _cancellation.Cancel();
    }
}