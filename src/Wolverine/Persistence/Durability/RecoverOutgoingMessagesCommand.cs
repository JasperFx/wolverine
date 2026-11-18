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
        var nodeNumber = runtime.Options.Durability.AssignedNodeNumber;

        // GH-5587. Sent on this destination's own lane, in the background, so a destination that is slow to accept
        // sends holds up neither the agents queue this command runs on nor any other destination's recovery. A
        // destination whose lane is still busy with an earlier pass is skipped, and nothing more is claimed for it
        // until it has taken what it already has.
        var lanes = DestinationRecovery.LanesFor(runtime);
        if (lanes == null)
        {
            await DestinationRecovery.RecoverOutgoingAsync(_store, _sendingAgent, nodeNumber, _logger, null,
                cancellationToken);
            return AgentCommands.Empty;
        }

        var token = runtime.Options.Durability.Cancellation;
        if (!lanes.TryStart(RecoveryDirection.Outgoing, _sendingAgent.Destination,
                () => DestinationRecovery.RecoverOutgoingAsync(_store, _sendingAgent, nodeNumber, _logger, lanes, token)))
        {
            _logger.LogDebug(
                "Skipping outbox recovery for {Destination} on this pass, because it is still sending the messages recovered for it earlier",
                _sendingAgent.Destination);
        }

        return AgentCommands.Empty;
    }
}
