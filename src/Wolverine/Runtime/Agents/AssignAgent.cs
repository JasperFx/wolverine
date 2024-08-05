using System.Text;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record AssignAgent(Uri AgentUri, NodeDestination Destination) : IAgentCommand
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        if (Destination.NodeId == runtime.Options.UniqueNodeId)
        {
            await runtime.Agents.StartLocallyAsync(AgentUri);
        }
        else
        {
            try
            {
                await runtime.Agents.InvokeAsync(Destination, new StartAgent(AgentUri));
            }
            catch (UnknownWolverineNodeException e)
            {
                runtime.Logger.LogWarning(e, "Error while trying to assign agent {AgentUri} to {NodeId}", AgentUri,
                    Destination.NodeId);
                return AgentCommands.Empty;
            }
        }

        runtime.Logger.LogInformation("Successfully started agent {AgentUri} on node {NodeId}", AgentUri, runtime.Options.Durability.AssignedNodeNumber);

        return AgentCommands.Empty;
    }

    public virtual bool Equals(AssignAgent? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return AgentUri.Equals(other.AgentUri) && Destination.NodeId.Equals(other.Destination.NodeId);
    }
}