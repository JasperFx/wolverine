using System.Data.Common;
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
    private readonly DurabilitySettings _settings;
    private readonly ILogger _logger;
    private readonly List<IncomingCount> _incoming = new();

    public CheckRecoverableIncomingMessagesOperation(IMessageDatabase database, IEndpointCollection endpoints, DurabilitySettings settings, ILogger logger)
    {
        _database = database;
        _endpoints = endpoints;
        _settings = settings;
        _logger = logger;
    }

    public string Description => "Recover persisted incoming messages";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append($"select {DatabaseConstants.ReceivedAt}, count(*) from {_database.SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} group by {DatabaseConstants.ReceivedAt};");
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

            // TODO -- log/tracker
            yield return new RecoverableIncomingMessagesOperation(_database, incoming, listener, _settings, _logger);
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
    private readonly IMessageDatabase _database;
    private readonly IncomingCount _count;
    private readonly IListenerCircuit _circuit;
    private readonly DurabilitySettings _settings;
    private readonly ILogger _logger;

    public RecoverableIncomingMessagesOperation(IMessageDatabase database, IncomingCount count,
        IListenerCircuit circuit, DurabilitySettings settings, ILogger logger)
    {
        _database = database;
        _count = count;
        _circuit = circuit;
        _settings = settings;
        _logger = logger;
    }
    
    public virtual int DeterminePageSize(IListenerCircuit listener, IncomingCount count,
        DurabilitySettings durabilitySettings)
    {
        if (listener!.Status != ListeningStatus.Accepting)
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

    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var pageSize = DeterminePageSize(_circuit, _count, _settings);
        if (pageSize == 0)
        {
            // TODO -- log here
            yield break;
        }
        
        // TODO - this will have to be changed in the underlying to not use the existing database session
        var envelopes = await _database.LoadPageOfGloballyOwnedIncomingAsync(_count.Destination, pageSize);
        await _database.ReassignIncomingAsync(_settings.NodeLockId, envelopes);
        
        _circuit.EnqueueDirectly(envelopes);
        _logger.RecoveredIncoming(envelopes);

        if (pageSize < _count.Count)
        {
            var count = _count with {Count = _count.Count - pageSize};

            yield return new RecoverableIncomingMessagesOperation(_database, count, _circuit, _settings, _logger);
        }
    }
}



