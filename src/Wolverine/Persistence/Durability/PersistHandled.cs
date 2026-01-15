using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence.Durability;

public class PersistHandled(Envelope Handled) : IAgentCommand
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        await runtime.Storage.Inbox.StoreIncomingAsync(Handled);
        
        return AgentCommands.Empty;
    }
}