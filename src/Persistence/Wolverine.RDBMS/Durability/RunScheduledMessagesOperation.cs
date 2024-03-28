using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.RDBMS.Durability;

internal class RunScheduledMessagesOperation : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly List<Envelope> _envelopes = new();
    private readonly ILocalReceiver _localQueue;
    private readonly DurabilitySettings _settings;

    public RunScheduledMessagesOperation(IMessageDatabase database, DurabilitySettings settings,
        ILocalReceiver localQueue)
    {
        _database = database;
        _settings = settings;
        _localQueue = localQueue;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        await _database.PollForScheduledMessagesAsync(_localQueue, runtime.Logger, _settings, cancellationToken);

        return AgentCommands.Empty;
    }
}