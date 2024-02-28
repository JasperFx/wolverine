using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController : IInternalHandler<NodeEvent>
{
    // Do assignments one by one, agent by agent
    public async IAsyncEnumerable<object> HandleAsync(NodeEvent @event)
    {
        _logger.LogInformation("Processing node event {Type} from node {OtherId} in node {NodeNumber}", @event.Node.Id,
            @event.Type, _tracker.Self!.AssignedNodeId);

        switch (@event.Type)
        {
            case NodeEventType.Exiting:
                _tracker.Remove(@event.Node);

                if (_tracker.Self.IsLeader())
                {
                    await _persistence.DeleteAsync(@event.Node.Id);
                    await requestAssignmentEvaluationAsync();
                }
                else if (_tracker.Leader == null || _tracker.Leader.Id == @event.Node.Id)
                {
                    var candidate = _tracker.OtherNodes().MinBy(x => x.AssignedNodeId);

                    if (candidate == null || candidate.AssignedNodeId > _tracker.Self.AssignedNodeId)
                    {
                        yield return new TryAssumeLeadership();
                    }
                    else
                    {
                        yield return new TryAssumeLeadership().ToNode(candidate);
                    }
                }

                break;

            case NodeEventType.Started:
                _tracker.Add(@event.Node);
                if (_tracker.Self.IsLeader())
                {
                    await requestAssignmentEvaluationAsync();
                }

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
    }
}