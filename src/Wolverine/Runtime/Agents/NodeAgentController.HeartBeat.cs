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

/* Notes
 DistributeEvenly should abort quickly if every node has either the ceiling or floor number
 No local messages should ever go through the transport
 Verify vs evaluate
 - If no dynamic, no nodes changed, do a verify
 
 
 
 
 */

public partial class NodeAgentController
{
    public async Task<AgentCommands> DoHealthChecksAsync()
    {
        if (_cancellation.IsCancellationRequested)
        {
            return AgentCommands.Empty;
        }

        if (_tracker.Self == null)
        {
            return AgentCommands.Empty;
        }

        // write health check regardless
        await _persistence.MarkHealthCheckAsync(_tracker.Self.Id);

        var nodes = await _persistence.LoadAllNodesAsync(_cancellation.Token);

        // Check for stale nodes that are no longer writing health checks
        var staleTime = DateTimeOffset.UtcNow.Subtract(_runtime.Options.Durability.StaleNodeTimeout);
        var staleNodes = nodes.Where(x => x.LastHealthCheck < staleTime).ToArray();
        nodes = nodes.Where(x => !staleNodes.Contains(x)).ToList();

        // Do it no matter what
        await ejectStaleNodes(staleNodes);

        if (_tracker.Self.IsLeader())
        {
            // TODO -- do the verification here too!
            return await EvaluateAssignmentsAsync(nodes);
        }

        return await tryElectNewLeaderIfNecessary(nodes, staleNodes);
    }

    private async Task<AgentCommands> tryElectNewLeaderIfNecessary(IReadOnlyList<WolverineNode> activeNodes,
        IReadOnlyList<WolverineNode> staleNodes)
    {
        // Clean out the dormant nodes first!!! 
        await ejectStaleNodes(staleNodes);
        
        // If there is no known leader, try to elect a newer one
        if (!activeNodes.Any(x => x.IsLeader()))
        {
            var candidate = activeNodes.MinBy(x => x.AssignedNodeId);

            if (candidate == null || candidate.AssignedNodeId > _tracker.Self.AssignedNodeId)
            {
                // Try to take leadership in this node
                return [new TryAssumeLeadership()];
            }

            // Ask another, older node to take leadership
            return [new TryAssumeLeadership(){CandidateId = candidate.Id}];
        }

        return AgentCommands.Empty;
    }

    private async Task ejectStaleNodes(IReadOnlyList<WolverineNode> staleNodes)
    {
        foreach (var staleNode in staleNodes)
        {
            await _persistence.DeleteAsync(staleNode.Id);
            _tracker.Remove(staleNode);
        }

        if (staleNodes.Any())
        {
            var records = staleNodes.Select(x => new NodeRecord
            {
                NodeNumber = x.AssignedNodeId,
                RecordType = NodeRecordType.DormantNodeEjected,
                Description = "Health check on Node " + _runtime.Options.Durability.AssignedNodeNumber
            }).ToArray();

            await _persistence.LogRecordsAsync(records);
        }
    }

    public void CancelHeartbeatChecking()
    {
        _cancellation.Cancel();
    }
}