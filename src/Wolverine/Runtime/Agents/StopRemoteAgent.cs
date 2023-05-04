namespace Wolverine.Runtime.Agents;

internal record StopRemoteAgent(Uri AgentUri, Guid NodeId) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
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

        yield break;
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
}