using Wolverine.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Records a change in state for the active nodes within this Wolverine system
/// </summary>
/// <param name="Node"></param>
/// <param name="Type"></param>
public record NodeEvent(WolverineNode Node, NodeEventType Type) : IWolverineEvent, IInternalMessage
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public virtual bool Equals(NodeEvent? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Node.Equals(other.Node) && Type == other.Type;
    }

    public void ModifyState(WolverineTracker tracker)
    {
        switch (Type)
        {
            case NodeEventType.Started:
                tracker.Nodes[Node.Id] = Node;
                break;

            case NodeEventType.Exiting:
                if (tracker.Nodes.TryGetValue(Node.Id, out var existing))
                {
                    foreach (var uri in existing.ActiveAgents) tracker.Agents.Remove(uri);
                }

                foreach (var uri in Node.ActiveAgents) tracker.Agents[uri] = null;

                tracker.Nodes.Remove(Node.Id);

                if (tracker.Leader?.Id == Node.Id)
                {
                    tracker.Leader = null;
                }

                break;

            case NodeEventType.LeadershipAssumed:
                if (tracker.Leader != null)
                {
                    tracker.Leader.ActiveAgents.Remove(NodeAgentController.LeaderUri);
                }

                tracker.Nodes[Node.Id] = Node;
                tracker.Leader = Node;
                Node.ActiveAgents.Add(NodeAgentController.LeaderUri);
                break;
        }
    }

    public override string ToString()
    {
        return $"Node: {Node.Id}, {nameof(Type)}: {Type}";
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Node, (int)Type);
    }
}