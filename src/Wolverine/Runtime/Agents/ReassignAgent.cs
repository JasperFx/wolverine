using System.Text;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record ReassignAgent(Uri AgentUri, NodeDestination OriginalNode, NodeDestination ActiveNode) : IAgentCommand
{
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