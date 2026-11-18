using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

public class RecoverIncomingMessagesCommand : IAgentCommand
{
    private readonly IListenerCircuit _circuit;
    private readonly IncomingCount _count;
    private readonly IMessageStore _store;
    private readonly ILogger _logger;
    private readonly DurabilitySettings _settings;

    public RecoverIncomingMessagesCommand(IMessageStore store, IncomingCount count,
        IListenerCircuit circuit, DurabilitySettings settings, ILogger logger)
    {
        _store = store;
        _count = count;
        _circuit = circuit;
        _settings = settings;
        _logger = logger;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        // GH-5587. The recovery runs on this destination's own lane, in the background, so a listener that is slow
        // to take what is recovered for it holds up neither the agents queue this command runs on nor any other
        // destination's recovery. A destination whose lane is still busy with an earlier pass is skipped, and nothing
        // more is claimed for it until it has taken what it already has.
        var lanes = DestinationRecovery.LanesFor(runtime);
        if (lanes == null)
        {
            await recoverAsync(null, cancellationToken);
            return AgentCommands.Empty;
        }

        if (!lanes.TryStart(RecoveryDirection.Incoming, _count.Destination,
                () => recoverAsync(lanes, _settings.Cancellation)))
        {
            _logger.LogDebug(
                "Skipping inbox recovery for {Destination} on this pass, because it has not yet taken the messages recovered for it earlier",
                _count.Destination);
        }

        return AgentCommands.Empty;
    }

    private Task recoverAsync(RecoveryLanes? lanes, CancellationToken token)
    {
        return DestinationRecovery.RecoverIncomingAsync(_store, _count.Destination, _circuit, _count.Count,
            remaining => DeterminePageSize(_circuit, _count with { Count = remaining }, _settings), _settings, _logger,
            lanes, token);
    }

    public virtual int DeterminePageSize(IListenerCircuit listener, IncomingCount count,
        DurabilitySettings durabilitySettings)
    {
        return PageSizeFor(listener, count.Count, durabilitySettings);
    }

    /// <summary>
    /// How many of <paramref name="available" /> recoverable envelopes the listener has room for right now. This is
    /// a snapshot, not a reservation.
    /// </summary>
    internal static int PageSizeFor(IListenerCircuit listener, int available, DurabilitySettings durabilitySettings)
    {
        if (listener.Status != ListeningStatus.Accepting)
        {
            return 0;
        }

        // GH-3590 defense in depth. Inbox recovery for single node listeners (Exclusive / PinnedToLeader) is
        // owned by the node that is actually hosting the listener, never by the per-database durability agent.
        // GH-3856: this MUST ask the same question as IEndpointCollection.IsSingleNodeListener(). Testing the
        // raw ListenerScope here meant a local queue carrying ListenerScope.Exclusive got a recovery command
        // issued and then silently recovered nothing, forever.
        if (listener.Endpoint.IsSingleNodeListener)
        {
            return 0;
        }

        var pageSize = durabilitySettings.RecoveryBatchSize;
        if (pageSize > available)
        {
            pageSize = available;
        }

        if (pageSize + listener.QueueCount > listener.Endpoint.BufferingLimits.Maximum)
        {
            pageSize = listener.Endpoint.BufferingLimits.Maximum - listener.QueueCount - 1;
        }

        if (pageSize < 0)
        {
            return 0;
        }

        return pageSize;
    }
}
