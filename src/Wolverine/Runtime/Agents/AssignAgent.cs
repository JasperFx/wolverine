using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

internal record AssignAgent(Uri AgentUri, Guid NodeId) : IAgentCommand, ISerializable
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
                runtime.Logger.LogWarning(e, "Error while trying to assign agent {AgentUri} to {NodeId}", AgentUri,
                    NodeId);
                yield break;
            }
        }

        runtime.Logger.LogInformation("Successfully started agent {AgentUri} on node {NodeId}", AgentUri, runtime.Options.Durability.AssignedNodeNumber);
        runtime.Tracker.Publish(new AgentStarted(NodeId, AgentUri));
    }

    public byte[] Write()
    {
        return NodeId.ToByteArray().Concat(Encoding.UTF8.GetBytes(AgentUri.ToString())).ToArray();
    }

    public static object Read(byte[] bytes)
    {
        var agentUriString = Encoding.UTF8.GetString(bytes.Skip(16).ToArray());
        return new AssignAgent(new Uri(agentUriString), new Guid(bytes.Take(16).ToArray()));
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

    public override string ToString()
    {
        return $"Assign agent {AgentUri} to node {NodeId}";
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(AgentUri, NodeId);
    }
}