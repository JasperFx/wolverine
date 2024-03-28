using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    public async Task<AgentCommands> StartLocalAgentProcessingAsync(WolverineOptions options)
    {
        var others = await _persistence.LoadAllNodesAsync(_cancellation);

        var current = WolverineNode.For(options);
        foreach (var controller in _agentFamilies.Values.OfType<IStaticAgentFamily>())
        {
            current.Capabilities.AddRange(await controller.SupportedAgentsAsync());
        }

        current.AssignedNodeId = await _persistence.PersistAsync(current, _cancellation);
        await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.NodeStarted));
        
        _runtime.Options.Durability.AssignedNodeNumber = current.AssignedNodeId;

        _logger.LogInformation("Starting agents for Node {NodeId} with assigned node id {Id}",
            options.UniqueNodeId, current.AssignedNodeId);

        _tracker.MarkCurrent(current);

        var commands = new AgentCommands();

        if (others.Any())
        {
            foreach (var other in others)
            {
                var active = _tracker.Add(other);
                
                commands.Add(new RemoteNodeEvent(current, NodeEventType.Started, active));
            }

            if (_tracker.Leader == null)
            {
                // Find the oldest, ask it to assume leadership
                var leaderCandidate = others.MinBy(x => x.AssignedNodeId) ?? current;

                _logger.LogInformation(
                    "Found no elected leader on node startup, requesting node {NodeId} to be the new leader",
                    leaderCandidate.AssignedNodeId);
                
                commands.Add(new TryAssumeLeadership { CurrentLeaderId = null, CandidateId = leaderCandidate.Id});
            }
        }
        else
        {
            _logger.LogInformation("Found no other existing nodes, deciding to assume leadership in node {NodeNumber}",
                options.Durability.AssignedNodeNumber);

            // send local command
            commands.Add(new TryAssumeLeadership { CurrentLeaderId = null });
        }

        HasStartedLocalAgentWorkflowForBalancedMode = true;

        return commands;
    }

    public bool HasStartedLocalAgentWorkflowForBalancedMode { get; private set; }
}