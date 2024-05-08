using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    // Do assignments one by one, agent by agent
    public async Task<AgentCommands> ApplyNodeEventAsync(NodeEvent @event)
    {
        _logger.LogInformation("Processing node event {Type} from node {OtherId} in node {NodeNumber}", @event.Node.Id,
            @event.Type, _tracker.Self!.AssignedNodeId);

        var commands = new AgentCommands();

        switch (@event.Type)
        {
            case NodeEventType.Exiting:
                _tracker.Remove(@event.Node);

                if (_tracker.Self.IsLeader())
                {
                    await _persistence.DeleteAsync(@event.Node.Id);
                }
                else if (_tracker.Leader == null || _tracker.Leader.Id == @event.Node.Id)
                {
                    var candidate = _tracker.OtherNodes().MinBy(x => x.AssignedNodeId);

                    if (candidate == null || candidate.AssignedNodeId > _tracker.Self.AssignedNodeId)
                    {
                        commands.Add(new TryAssumeLeadership());
                    }
                    else
                    {
                        commands.Add(new TryAssumeLeadership{CandidateId = candidate.Id});
                    }
                }

                break;

            case NodeEventType.Started:
                _tracker.Add(@event.Node);

                break;


            case NodeEventType.LeadershipAssumed:
                // Nothing actually, because publishing the event to the tracker will
                // happily change the state
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(@event));
        }

        // If the call above succeeded, this is low risk
        _tracker.Publish(@event);

        return commands;
    }
}