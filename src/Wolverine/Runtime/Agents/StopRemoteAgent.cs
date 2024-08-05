namespace Wolverine.Runtime.Agents;

internal record StopRemoteAgent(Uri AgentUri, NodeDestination Destination) : IAgentCommand
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        if (Destination.NodeId == runtime.Options.UniqueNodeId)
        {
            await runtime.Agents.StopLocallyAsync(AgentUri);
        }
        else
        {
            await runtime.Agents.InvokeAsync(Destination, new StopAgent(AgentUri));
        }

        return AgentCommands.Empty;
    }
}