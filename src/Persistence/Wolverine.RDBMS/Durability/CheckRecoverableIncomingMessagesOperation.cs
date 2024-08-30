using System.Data.Common;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Polling;
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
            var listener = _endpoints.FindListenerCircuit(incoming.Destination);

            if (listener.Status == ListeningStatus.Accepting)
            {
                _logger.LogInformation(
                    "Issuing a command to recover {Count} incoming messages from the inbox to destination {Destination}",
                    incoming.Count, incoming.Destination);
                yield return
                    new RecoverIncomingMessagesCommand(_database, incoming, listener, _settings, _logger);
            }
            else
            {
                _logger.LogInformation("Found {Count} incoming messages from the inbox to destination {Destination}, but the listener is latched", incoming.Count, incoming.Destination);
            }

        }
    }


}