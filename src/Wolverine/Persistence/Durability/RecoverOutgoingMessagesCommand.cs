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
        var expiredList = new List<Envelope>();
        var goodList = new List<Envelope>();
        foreach (var envelope in outgoing)
        {
            if (envelope.IsExpired())
                expiredList.Add(envelope);
            else
                goodList.Add(envelope);
        }

        var expiredMessages = expiredList.ToArray();
        var good = goodList.ToArray();

        await _store.Outbox.DiscardAndReassignOutgoingAsync(expiredMessages, good,
            runtime.Options.Durability.AssignedNodeNumber);

        foreach (var envelope in good)
        {
            // The sender wire tap is an in-memory-only reference that does not survive
            // persistence, so a recovered outgoing envelope has none. Re-attach it from
            // the sending agent's endpoint so RecordSuccessAsync still fires when the
            // recovered message is sent. See GH-3263.
            envelope.WireTap ??= _sendingAgent.Endpoint.WireTap;

            // The originating store is an in-memory-only reference too, for exactly the same
            // reason, and losing it is worse than losing the wire tap: DelegatingMessageOutbox
            // routes every acknowledgement on envelope.Store and falls back to the MAIN store
            // when it is null. An envelope recovered from an ancillary store would therefore be
            // deleted from the main store's outgoing table -- a no-op -- leaving the ancillary
            // row in place to be recovered, sent and handled again on every single restart.
            // Harmless for the main store, where _store.Outbox is already the fallback.
            // This is the outgoing twin of the stamp RecoverIncomingMessagesCommand has applied
            // since GH-2318. See GH-4417.
            envelope.Store ??= _store;

            await _sendingAgent.EnqueueOutgoingAsync(envelope);
        }

        _logger.LogInformation(
            "Recovered {Count} messages from outbox for destination {Destination} while discarding {ExpiredCount} expired messages",
            good.Length, _sendingAgent.Destination, expiredMessages.Length);

        return AgentCommands.Empty;
    }
}