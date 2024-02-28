using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController : IInternalHandler<StartLocalAgentProcessing>
{
    public async IAsyncEnumerable<object> HandleAsync(StartLocalAgentProcessing command)
    {
        var others = await _persistence.LoadAllNodesAsync(_cancellation);

        var current = WolverineNode.For(command.Options);
        foreach (var controller in _agentFamilies.Values.OfType<IStaticAgentFamily>())
        {
            current.Capabilities.AddRange(await controller.SupportedAgentsAsync());
        }

        current.AssignedNodeId = await _persistence.PersistAsync(current, _cancellation);
        await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.NodeStarted));
        
        _runtime.Options.Durability.AssignedNodeNumber = current.AssignedNodeId;

        _logger.LogInformation("Starting agents for Node {NodeId} with assigned node id {Id}",
            command.Options.UniqueNodeId, current.AssignedNodeId);

        _tracker.MarkCurrent(current);

        if (others.Any())
        {
            foreach (var other in others)
            {
                var active = _tracker.Add(other);
                yield return new NodeEvent(current, NodeEventType.Started).ToNode(active);
            }

            if (_tracker.Leader == null)
            {
                // Find the oldest, ask it to assume leadership
                var leaderCandidate = others.MinBy(x => x.AssignedNodeId) ?? current;

                _logger.LogInformation(
                    "Found no elected leader on node startup, requesting node {NodeId} to be the new leader",
                    leaderCandidate.AssignedNodeId);

                yield return new TryAssumeLeadership { CurrentLeaderId = null }.ToNode(leaderCandidate);
            }
        }
        else
        {
            _logger.LogInformation("Found no other existing nodes, deciding to assume leadership in node {NodeNumber}",
                command.Options.Durability.AssignedNodeNumber);

            // send local command
            yield return new TryAssumeLeadership { CurrentLeaderId = null };
        }

        HasStartedLocalAgentWorkflowForBalancedMode = true;
    }

    public bool HasStartedLocalAgentWorkflowForBalancedMode { get; private set; }
}