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
            $"select {DatabaseConstants.ReceivedAt}, count(*) from {_database.TableNameFor(DatabaseConstants.IncomingTable)} where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} group by {DatabaseConstants.ReceivedAt};");
    }

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        while (await reader.ReadAsync(token))
        {
            var address =
                new Uri(await reader.GetFieldValueAsync<string>(0, token).ConfigureAwait(false));
            // GH-4480: a provider picks its own CLR type for count(*) -- Oracle's unconstrained NUMBER
            // arrives as decimal or Int64 -- and GetFieldValueAsync<int> is a cast that throws on both.
            var count = await reader.GetInt32TolerantlyAsync(1, token).ConfigureAwait(false);

            var incoming = new IncomingCount(address, count);

            _incoming.Add(incoming);
        }
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        if (_settings.Cancellation.IsCancellationRequested) yield break;
        
        foreach (var incoming in _incoming)
        {
            // GH-3590: exclusive and leader-pinned listeners are only active on ONE node, while this durability
            // agent is assigned per database and may well be running somewhere else. GH-4776: a global
            // partition's companion local queue is the same problem wearing a local:// address -- it exists on
            // every node, but only the slot's owner may execute from it. Recovery for both is owned by the
            // listening node itself (ListenerInboxRecovery). This check has to happen BEFORE any circuit lookup,
            // because FindListenerCircuit() falls back to the durable local queue and would otherwise mis-route
            // another node's messages into this node's local queue.
            if (_endpoints.ListenerOwnsItsInboxRecovery(incoming.Destination))
            {
                continue;
            }

            var listener = _endpoints.FindListenerCircuit(incoming.Destination);
            if (listener == null)
            {
                // This *might* happen during shutdown, but GH-4296 and GH-4807 were both the other case:
                // an inbox row stamped with an address that nothing is registered under, skipped on every
                // pass with no log line at all, so the messages sat at Incoming forever and the only
                // evidence was in the database. Both sibling branches below log every pass; so does this
                // one now.
                _logger.LogWarning(
                    "Found {Count} incoming messages in the inbox for destination {Destination}, but no listening endpoint could be resolved for that address. These messages cannot be recovered and will stay in the inbox until an endpoint listening at that address exists",
                    incoming.Count, incoming.Destination);
                continue;
            }

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