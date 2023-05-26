using System.Runtime.CompilerServices;

namespace Wolverine.Runtime.Agents;

public record CheckAgentHealth : IInternalMessage;

public record VerifyAssignments : IInternalMessage;


public partial class NodeAgentController : IInternalHandler<CheckAgentHealth>, IInternalHandler<VerifyAssignments>
{
    private DateTimeOffset? _lastAssignmentCheck;
    
    public async IAsyncEnumerable<object> HandleAsync(CheckAgentHealth message)
    {
        if (_cancellation.IsCancellationRequested) yield break;
        if (_tracker.Self == null) yield break;
        
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
    
    public async IAsyncEnumerable<object> HandleAsync(VerifyAssignments message)
    {
        if (LastAssignments == null) yield break;
        
        var dict = new Dictionary<Uri, Guid>();

        var requests = _tracker.OtherNodes()
            .Select(node => _runtime.Agents.InvokeAsync<RunningAgents>(node.Id, new QueryAgents())).ToArray();
        
        // Loop and find. 
        foreach (var request in requests)
        {
            var result = await request;
            foreach (var agentUri in result.Agents)
            {
                dict[agentUri] = result.NodeId;
            }
        }

        var delta = LastAssignments.FindDelta(dict);

        foreach (var command in delta)
        {
            yield return command;
        }
        
        _lastAssignmentCheck = DateTimeOffset.UtcNow;
    }
}

internal class QueryAgents : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agents = runtime.Agents.AllRunningAgentUris();
        yield return new RunningAgents(runtime.Options.UniqueNodeId, agents);
    }
}

internal record RunningAgents(Guid NodeId, Uri[] Agents);