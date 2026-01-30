using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.RDBMS.Durability;

internal class RunScheduledMessagesOperation : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly DurabilitySettings _settings;

    public RunScheduledMessagesOperation(IMessageDatabase database, DurabilitySettings settings)
    {
        _database = database;
        _settings = settings;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        await _database.PollForScheduledMessagesAsync(runtime, runtime.Logger, _settings, cancellationToken);

        return AgentCommands.Empty;
    }
}