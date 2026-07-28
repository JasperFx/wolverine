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
        var pageSize = DeterminePageSize(_circuit, _count, _settings);
        if (pageSize == 0)
        {
            _logger.LogInformation(
                "Unable to recover inbox messages to destination {Destination}. Listener has status {Status}, queued count {QueuedCount}, and BufferingLimits {BufferedLimits}",
                _count.Destination, _circuit.Status, _circuit.QueueCount, _circuit.Endpoint.BufferingLimits);
            return AgentCommands.Empty;
        }

        var envelopes = await _store.LoadPageOfGloballyOwnedIncomingAsync(_count.Destination, pageSize);

        // Ensure each recovered envelope carries a reference to the store it was loaded from.
        // This is critical for ancillary stores: without this, the envelope's Store property
        // is null and DelegatingMessageInbox falls back to the main store when marking the
        // envelope as handled — leaving it stuck as "Incoming" in the ancillary store.
        // See https://github.com/JasperFx/wolverine/issues/2318
        foreach (var envelope in envelopes)
        {
            envelope.Store ??= _store;
        }

        await _store.ReassignIncomingAsync(_settings.AssignedNodeNumber, envelopes);

        // GH-3680. The rows above are now owned by *this* node, and orphan recovery only ever releases
        // rows owned by a node it has proven to be dead. If the hand-off into the listener fails -- and it
        // absolutely can, because a circuit breaker trip between DeterminePageSize() and here nulls out the
        // agent's receiver -- then nothing on any node will ever release them again and the messages are
        // lost for the life of the process. Hand ownership back so the next sweep can retry.
        try
        {
            await _circuit.EnqueueDirectlyAsync(envelopes);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error trying to enqueue {Count} recovered messages into the listener for {Destination}. Releasing them back to any node so that they are recovered on a later sweep",
                envelopes.Count, _count.Destination);

            await releaseAsync(envelopes);

            return AgentCommands.Empty;
        }

        _logger.RecoveredIncoming(envelopes);

        _logger.LogInformation("Successfully recovered {Count} messages from the inbox for listener {Listener}",
            envelopes.Count, _count.Destination);

        if (pageSize < _count.Count)
        {
            var count = _count with { Count = _count.Count - pageSize };

            return [new RecoverIncomingMessagesCommand(_store, count, _circuit, _settings, _logger)];
        }

        return AgentCommands.Empty;
    }

    private async Task releaseAsync(IReadOnlyList<Envelope> envelopes)
    {
        try
        {
            await _store.ReassignIncomingAsync(TransportConstants.AnyNode, envelopes);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error trying to release {Count} un-enqueued messages for {Destination} back to any node",
                envelopes.Count, _count.Destination);
        }
    }

    public virtual int DeterminePageSize(IListenerCircuit listener, IncomingCount count,
        DurabilitySettings durabilitySettings)
    {
        if (listener.Status != ListeningStatus.Accepting)
        {
            return 0;
        }

        // GH-3590 defense in depth. Inbox recovery for single node listeners (Exclusive / PinnedToLeader) is
        // owned by the node that is actually hosting the listener, never by the per-database durability agent.
        if (listener.Endpoint.ListenerScope != ListenerScope.CompetingConsumers)
        {
            return 0;
        }

        var pageSize = durabilitySettings.RecoveryBatchSize;
        if (pageSize > count.Count)
        {
            pageSize = count.Count;
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