using System.Runtime.CompilerServices;
using System.Text;

namespace Wolverine.Runtime.Agents;

internal record StopRemoteAgent(Uri AgentUri, Guid NodeId) : IAgentCommand, ISerializable
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        if (NodeId == runtime.Options.UniqueNodeId)
        {
            await runtime.Agents.StopLocallyAsync(AgentUri);
        }
        else
        {
            await runtime.Agents.InvokeAsync(NodeId, new StopAgent(AgentUri));
        }

        runtime.Tracker.Publish(new AgentStopped(AgentUri));

        return AgentCommands.Empty;
    }

    public virtual bool Equals(StopRemoteAgent? other)
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

    public override string ToString()
    {
        return $"{nameof(AgentUri)}: {AgentUri}, {nameof(NodeId)}: {NodeId}";
    }
    
    public byte[] Write()
    {
        return NodeId.ToByteArray().Concat(Encoding.UTF8.GetBytes(AgentUri.ToString())).ToArray();
    }

    public static object Read(byte[] bytes)
    {
        var agentUriString = Encoding.UTF8.GetString(bytes.Skip(16).ToArray());
        return new StopRemoteAgent(new Uri(agentUriString), new Guid(bytes.Take(16).ToArray()));
    }
}