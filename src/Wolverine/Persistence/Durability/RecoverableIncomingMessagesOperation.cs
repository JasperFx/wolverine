using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

public class RecoverableIncomingMessagesOperation : IAgentCommand
{
    private readonly IListenerCircuit _circuit;
    private readonly IncomingCount _count;
    private readonly IMessageStore _store;
    private readonly ILogger _logger;
    private readonly DurabilitySettings _settings;

    public RecoverableIncomingMessagesOperation(IMessageStore store, IncomingCount count,
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
                "Unable to recover inbox messages to destination {Destination}. Listener has status {Status} and queued count {QueuedCount}",
                _count.Destination, _circuit.Status, _circuit.QueueCount);
            return AgentCommands.Empty;
        }

        var envelopes = await _store.LoadPageOfGloballyOwnedIncomingAsync(_count.Destination, pageSize);
        await _store.ReassignIncomingAsync(_settings.AssignedNodeNumber, envelopes);

        _circuit.EnqueueDirectly(envelopes);
        _logger.RecoveredIncoming(envelopes);

        _logger.LogInformation("Successfully recovered {Count} messages from the inbox for listener {Listener}",
            envelopes.Count, _count.Destination);

        if (pageSize < _count.Count)
        {
            var count = _count with { Count = _count.Count - pageSize };

            return [new RecoverableIncomingMessagesOperation(_store, count, _circuit, _settings, _logger)];
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