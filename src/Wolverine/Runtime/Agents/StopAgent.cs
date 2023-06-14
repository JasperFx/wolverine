using System.Runtime.CompilerServices;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Command message used to stop and un-assign an agent on a running node
/// </summary>
/// <param name="AgentUri"></param>
internal record StopAgent(Uri AgentUri) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await runtime.Agents.StopLocallyAsync(AgentUri);
        yield break;
    }

    public virtual bool Equals(StopAgent? other)
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
        return $"Stop agent {AgentUri}";
    }
}