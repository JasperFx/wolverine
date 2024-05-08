using Wolverine.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Records the deactivation of an agent on a Wolverine node
/// </summary>
/// <param name="AgentUri"></param>
public record AgentStopped(Uri AgentUri) : IWolverineEvent
{
    public virtual bool Equals(AgentStopped? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return AgentUri.Equals(other.AgentUri);
    }

    public void ModifyState(WolverineTracker tracker)
    {
        var node = tracker.FindOwnerOfAgent(AgentUri);
        node?.ActiveAgents.Remove(AgentUri);

        tracker.Agents.TryRemove(AgentUri, out var value);
    }

    public override int GetHashCode()
    {
        return AgentUri.GetHashCode();
    }

    public override string ToString()
    {
        return $"Agent {AgentUri} has stopped";
    }
}