using System.Text;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record ReassignAgent(Uri AgentUri, NodeDestination OriginalNode, NodeDestination ActiveNode) : IAgentCommand
{
    // Two nodes are involved, but only the stop half runs inline here -- the start is cascaded back to the
    // drain as a separate AssignAgent, which lands in ActiveNode's lane on the next round. Keying the whole
    // command to ActiveNode keeps a reassignment behind any other work already queued for the node that is
    // about to take the agent.
    public Guid? DestinationNodeId => ActiveNode.NodeId;

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.Agents.InvokeAsync(OriginalNode, new StopAgent(AgentUri));
        }
        catch (UnknownWolverineNodeException e)
        {
            runtime.Logger.LogWarning(e,
                "Error trying to reassign a running agent {AgentUri} from {CurrentNodeId} to {NewNodeId}", AgentUri,
                OriginalNode, ActiveNode);
            return AgentCommands.Empty;
        }

        runtime.Logger.LogInformation("Successfully stopped agent {Agent} on node {OriginalNode}", AgentUri,
            OriginalNode.NodeId);

        // Do this in separate steps
        return [new AssignAgent(AgentUri, ActiveNode)];
    }
}