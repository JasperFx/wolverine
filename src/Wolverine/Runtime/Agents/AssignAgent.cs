using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record AssignAgent(Uri AgentUri, Guid NodeId) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (NodeId == runtime.Options.UniqueNodeId)
        {
            await runtime.Agents.StartLocallyAsync(AgentUri);
        }
        else
        {
            try
            {
                await runtime.Agents.InvokeAsync(NodeId, new StartAgent(AgentUri));
            }
            catch (UnknownWolverineNodeException e)
            {
                runtime.Logger.LogWarning(e, "Error while trying to assign agent {AgentUri} to {NodeId}", AgentUri, NodeId);
                yield break;
            }
        }
        
        runtime.Logger.LogInformation("Successfully started agent {AgentUri} on node {NodeId}", AgentUri, NodeId);
        runtime.Tracker.Publish(new AgentStarted(NodeId, AgentUri));
    }

    public override string ToString()
    {
        return $"Assign agent {AgentUri} to node {NodeId}";
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

        return AgentUri.Equals(other.AgentUri) && NodeId.Equals(other.NodeId);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(AgentUri, NodeId);
    }
}