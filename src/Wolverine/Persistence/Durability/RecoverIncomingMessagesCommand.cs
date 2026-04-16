using Microsoft.Extensions.Logging;
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

        await _circuit.EnqueueDirectlyAsync(envelopes);
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

    public virtual int DeterminePageSize(IListenerCircuit listener, IncomingCount count,
        DurabilitySettings durabilitySettings)
    {
        if (listener.Status != ListeningStatus.Accepting)
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