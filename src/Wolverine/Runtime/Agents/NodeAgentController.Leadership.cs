using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    public async Task<AgentCommands> AssumeLeadershipAsync(Guid? currentLeaderId)
    {
        if (_tracker.Self!.IsLeader())
        {
            _logger.LogInformation("Already the current leader ({NodeId}), ignoring the request to assume leadership",
                _tracker.Self.AssignedNodeId);
            return AgentCommands.Empty;
        }

        await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.ElectionRequested));

        var assigned = await _persistence.MarkNodeAsLeaderAsync(currentLeaderId, _tracker.Self!.Id);

        var commands = new AgentCommands();

        if (assigned.HasValue)
        {
            if (assigned == _tracker.Self.Id)
            {
                _logger.LogInformation("Node {NodeNumber} successfully assumed leadership", _tracker.Self.AssignedNodeId);
                await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options,
                    NodeRecordType.LeadershipAssumed, LeaderUri));

                var all = await _persistence.LoadAllNodesAsync(_cancellation.Token);
                var others = all.Where(x => x.Id != _tracker.Self.Id).ToArray();
                foreach (var other in others)
                {
                    _tracker.Add(other);
                    commands.Add(new RemoteNodeEvent(_tracker.Self, NodeEventType.LeadershipAssumed, other));
                }

                _tracker.Publish(new NodeEvent(_tracker.Self, NodeEventType.LeadershipAssumed));

                foreach (var controller in _agentFamilies.Values)
                {
                    var agents = await controller.AllKnownAgentsAsync();
                    _tracker.RegisterAgents(agents);
                }
            }
            else
            {
                var leader = await _persistence.LoadNodeAsync(assigned.Value, _cancellation.Token);

                if (leader != null)
                {
                    _logger.LogInformation(
                        "Tried to assume leadership at node {NodeNumber}, but another node {LeaderId} has assumed leadership beforehand",
                        _tracker.Self.AssignedNodeId, assigned.Value);
                    _tracker.Publish(new NodeEvent(leader, NodeEventType.LeadershipAssumed));
                }
                else
                {
                    // The referenced leader doesn't exist -- which shouldn't happen, but real life, so try again...
                    commands.Add(new TryAssumeLeadership());
                }
            }

            return commands;
        }

        _logger.LogInformation("Node {NodeNumber} was unable to assume leadership, and no leader was found",
            _tracker.Self.AssignedNodeId);

        // Try it again
        commands.Add(new TryAssumeLeadership());
        return commands;
    }
}