using Wolverine.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
/// Records the activation of an individual agent on a Wolverine node
/// </summary>
/// <param name="NodeId"></param>
/// <param name="AgentUri"></param>
public record AgentStarted(Guid NodeId, Uri AgentUri) : IWolverineEvent
{
    public void ModifyState(WolverineTracker tracker)
    {
        new AgentStopped(AgentUri).ModifyState(tracker);

        tracker.Agents[AgentUri] = NodeId;

        if (tracker.Nodes.TryGetValue(NodeId, out var assignedNode))
        {
            assignedNode.ActiveAgents.Add(AgentUri);
        }
    }
}