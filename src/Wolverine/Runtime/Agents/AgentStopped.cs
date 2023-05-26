using Wolverine.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
/// Records the deactivation of an agent on a Wolverine node
/// </summary>
/// <param name="AgentUri"></param>
public record AgentStopped(Uri AgentUri) : IWolverineEvent
{
    public void ModifyState(WolverineTracker tracker)
    {
        var node = tracker.FindOwnerOfAgent(AgentUri);
        if (node != null)
        {
            node.ActiveAgents.Remove(AgentUri);
        }

        tracker.Agents.Remove(AgentUri);
    }

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

    public override int GetHashCode()
    {
        return AgentUri.GetHashCode();
    }

    public override string ToString()
    {
        return $"Agent {AgentUri} has stopped";
    }
}