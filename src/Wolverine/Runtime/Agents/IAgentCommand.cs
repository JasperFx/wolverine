namespace Wolverine.Runtime.Agents;

/// <summary>
///     Used for self-contained actions from agents to be executed in background, local
///     queues with Wolverine infrastructure
/// </summary>
public interface IAgentCommand
{
    IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken);
}