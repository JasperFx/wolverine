using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class CheckRecoverableIncomingMessagesOperation : IDatabaseOperation
{
    private readonly IMessageDatabase _database;
    private readonly IEndpointCollection _endpoints;
    private readonly List<IncomingCount> _incoming = new();
    private readonly ILogger _logger;
    private readonly DurabilitySettings _settings;

    public CheckRecoverableIncomingMessagesOperation(IMessageDatabase database, IEndpointCollection endpoints,
        DurabilitySettings settings, ILogger logger)
    {
        _database = database;
        _endpoints = endpoints;
        _settings = settings;
        _logger = logger;
    }

    public string Description => "Recover persisted incoming messages";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append(
            $"select {DatabaseConstants.ReceivedAt}, count(*) from {_database.SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} group by {DatabaseConstants.ReceivedAt};");
    }

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        while (await reader.ReadAsync(token))
        {
            var address =
                new Uri(await reader.GetFieldValueAsync<string>(0, token).ConfigureAwait(false));
            var count = await reader.GetFieldValueAsync<int>(1, token).ConfigureAwait(false);

            var incoming = new IncomingCount(address, count);

            _incoming.Add(incoming);
        }
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        foreach (var incoming in _incoming)
        {
            var listener = findListenerCircuit(incoming);

            if (listener.Status == ListeningStatus.Accepting)
            {
                _logger.LogInformation(
                    "Issuing a command to recover {Count} incoming messages from the inbox to destination {Destination}",
                    incoming.Count, incoming.Destination);
                yield return
                    new RecoverableIncomingMessagesOperation(_database, incoming, listener, _settings, _logger);
            }
            else
            {
                _logger.LogInformation("Found {Count} incoming messages from the inbox to destination {Destination}, but the listener is latched", incoming.Count, incoming.Destination);
            }

        }
    }

    private IListenerCircuit findListenerCircuit(IncomingCount count)
    {
        if (count.Destination.Scheme == TransportConstants.Local)
        {
            return (IListenerCircuit)_endpoints.GetOrBuildSendingAgent(count.Destination);
        }

        var listener = _endpoints.FindListeningAgent(count.Destination) ??
                       _endpoints.FindListeningAgent(TransportConstants.Durable);
        return listener!;
    }
}

internal class RecoverableIncomingMessagesOperation : IAgentCommand
{
    private readonly IListenerCircuit _circuit;
    private readonly IncomingCount _count;
    private readonly IMessageDatabase _database;
    private readonly ILogger _logger;
    private readonly DurabilitySettings _settings;

    public RecoverableIncomingMessagesOperation(IMessageDatabase database, IncomingCount count,
        IListenerCircuit circuit, DurabilitySettings settings, ILogger logger)
    {
        _database = database;
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

        var envelopes = await _database.LoadPageOfGloballyOwnedIncomingAsync(_count.Destination, pageSize);
        await _database.ReassignIncomingAsync(_settings.AssignedNodeNumber, envelopes);

        _circuit.EnqueueDirectly(envelopes);
        _logger.RecoveredIncoming(envelopes);

        _logger.LogInformation("Successfully recovered {Count} messages from the inbox for listener {Listener}",
            envelopes.Count, _count.Destination);

        if (pageSize < _count.Count)
        {
            var count = _count with { Count = _count.Count - pageSize };

            return [new RecoverableIncomingMessagesOperation(_database, count, _circuit, _settings, _logger)];
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