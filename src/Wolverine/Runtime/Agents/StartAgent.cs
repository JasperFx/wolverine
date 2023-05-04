using System.Runtime.CompilerServices;

namespace Wolverine.Runtime.Agents;

/// <summary>
/// Command message used to start and assign an agent to a node
/// </summary>
/// <param name="AgentUri"></param>
internal record StartAgent(Uri AgentUri) : IAgentCommand
{
    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await runtime.Agents.StartLocallyAsync(AgentUri);
        yield break;
    }
}