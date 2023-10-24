namespace Wolverine.Runtime.Agents;

public record CheckAgentHealth : IInternalMessage;

public record VerifyAssignments : IInternalMessage;

public partial class NodeAgentController : IInternalHandler<CheckAgentHealth>
{
    private DateTimeOffset? _lastAssignmentCheck;

    public async IAsyncEnumerable<object> HandleAsync(CheckAgentHealth message)
    {
        if (_cancellation.IsCancellationRequested)
        {
            yield break;
        }

        if (_tracker.Self == null)
        {
            yield break;
        }

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
                    
                yield return new EvaluateAssignments();
            }

            if (_lastAssignmentCheck != null && DateTimeOffset.UtcNow >
                _lastAssignmentCheck.Value.Add(_runtime.Options.Durability.CheckAssignmentPeriod))
            {
                yield return new VerifyAssignments();
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
                    yield return new TryAssumeLeadership();
                }
                else
                {
                    // Ask another, older node to take leadership
                    yield return new TryAssumeLeadership().ToNode(candidate);
                }
            }
        }
    }
}