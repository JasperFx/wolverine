namespace Wolverine.Runtime.Agents;

public record CheckAgentHealth : IAgentCommand
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return runtime.Agents.DoHealthChecksAsync();
    }
}

public partial class NodeAgentController 
{
    private DateTimeOffset? _lastAssignmentCheck;

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

        var commands = new AgentCommands();

        // write health check regardless
        await _persistence.MarkHealthCheckAsync(_tracker.Self.Id);

        // Check for stale nodes that are no longer writing health checks
        var staleTime = DateTimeOffset.UtcNow.Subtract(_runtime.Options.Durability.StaleNodeTimeout);
        var staleNodes = await _persistence.LoadAllStaleNodesAsync(staleTime, _cancellation);

        if (_tracker.Self.IsLeader())
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

            if (_lastAssignmentCheck != null && DateTimeOffset.UtcNow >
                _lastAssignmentCheck.Value.Add(_runtime.Options.Durability.CheckAssignmentPeriod))
            {
                commands.Add(new VerifyAssignments());
            }
        }
        else
        {
            var leaderNode = staleNodes.FirstOrDefault(x => x.Id == _tracker.Leader?.Id);
            if (leaderNode != null)
            {
                await _persistence.DeleteAsync(leaderNode.Id);
                _tracker.Remove(leaderNode);
            }

            // If there is no known leader, try to elect a newer one
            if (_tracker.Leader == null)
            {
                var candidate = _tracker.OtherNodes().MinBy(x => x.AssignedNodeId);

                if (candidate == null || candidate.AssignedNodeId > _tracker.Self.AssignedNodeId)
                {
                    // Try to take leadership in this node
                    commands.Add(new TryAssumeLeadership());
                }
                else
                {
                    // Ask another, older node to take leadership
                    commands.Add(new TryAssumeLeadership(){CandidateId = candidate.Id});
                }
            }
        }
        
        // We want this to be evaluated no matter what
        commands.Add(new EvaluateAssignments(this));

        return commands;
    }
}