using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports.Sending;

namespace Wolverine.Persistence.Durability;

public class RecoverOutgoingMessagesCommand : IAgentCommand
{
    private readonly IMessageStore _store;
    private readonly ILogger _logger;
    private readonly ISendingAgent _sendingAgent;

    public RecoverOutgoingMessagesCommand(ISendingAgent sendingAgent, IMessageStore store, ILogger logger)
    {
        _sendingAgent = sendingAgent;
        _store = store;
        _logger = logger;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        // It's possible that this could happen between the command being created and executed
        if (_sendingAgent.Latched)
        {
            return AgentCommands.Empty;
        }

        var outgoing = await _store.Outbox.LoadOutgoingAsync(_sendingAgent.Destination);
        var expiredMessages = outgoing.Where(x => x.IsExpired()).ToArray();
        var good = outgoing.Where(x => !x.IsExpired()).ToArray();

        await _store.Outbox.DiscardAndReassignOutgoingAsync(expiredMessages, good,
            runtime.Options.Durability.AssignedNodeNumber);

        foreach (var envelope in good) await _sendingAgent.EnqueueOutgoingAsync(envelope);

        _logger.LogInformation(
            "Recovered {Count} messages from outbox for destination {Destination} while discarding {ExpiredCount} expired messages",
            good.Length, _sendingAgent.Destination, expiredMessages.Length);

        return AgentCommands.Empty;
    }
}