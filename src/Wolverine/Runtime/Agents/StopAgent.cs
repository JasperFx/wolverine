using System.Runtime.CompilerServices;
using System.Text;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Command message used to stop and un-assign an agent on a running node
/// </summary>
/// <param name="AgentUri"></param>
internal record StopAgent(Uri AgentUri) : IAgentCommand, ISerializable
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        await runtime.Agents.StopLocallyAsync(AgentUri);
        return AgentCommands.Empty;
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

    public byte[] Write()
    {
        return Encoding.UTF8.GetBytes(AgentUri.ToString());
    }

    public static object Read(byte[] bytes)
    {
        return new StopAgent(new Uri(Encoding.UTF8.GetString(bytes)));
    }
}