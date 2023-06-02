using System.Data.Common;
using System.Runtime.CompilerServices;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports.Sending;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class CheckRecoverableOutgoingMessagesOperation : IDatabaseOperation
{
    private readonly IMessageDatabase _database;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;
    private readonly List<Uri> _uris = new();

    public CheckRecoverableOutgoingMessagesOperation(IMessageDatabase database, IWolverineRuntime runtime,
        ILogger logger)
    {
        _database = database;
        _runtime = runtime;
        _logger = logger;
    }

    public string Description => "Recover persisted outgoing messages";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append($"select distinct destination from {_database.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = 0;");
    }

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        while (await reader.ReadAsync(token))
        {
            var raw = await reader.GetFieldValueAsync<string>(0, token);
            _uris.Add(raw.ToUri());
        }
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        foreach (var destination in _uris)
        {
            ISendingAgent sendingAgent;
            try
            {
                sendingAgent = _runtime.Endpoints.GetOrBuildSendingAgent(destination);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to find a sending agent for {Destination}", destination);
                yield break;
            }

            if (!sendingAgent.Latched)
            {
                _logger.LogInformation("Found recoverable outgoing messages in the outbox for {Destination}", destination);
                yield return new RecoverOutgoingMessagesCommand(sendingAgent, _database, _logger);
            }
        }
    }
}

internal class RecoverOutgoingMessagesCommand : IAgentCommand
{
    private readonly ISendingAgent _sendingAgent;
    private readonly IMessageDatabase _database;
    private readonly ILogger _logger;

    public RecoverOutgoingMessagesCommand(ISendingAgent sendingAgent, IMessageDatabase database, ILogger logger)
    {
        _sendingAgent = sendingAgent;
        _database = database;
        _logger = logger;
    }

    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // It's possible that this could happen between the command being created and executed
        if (_sendingAgent.Latched) yield break;
        
        var outgoing = await _database.Outbox.LoadOutgoingAsync(_sendingAgent.Destination);
        var expiredMessages = outgoing.Where(x => x.IsExpired()).ToArray();
        var good = outgoing.Where(x => !x.IsExpired()).ToArray();

        await _database.Outbox.DiscardAndReassignOutgoingAsync(expiredMessages, good,
            runtime.Options.Durability.AssignedNodeNumber);

        foreach (var envelope in good)
        {
            await _sendingAgent.EnqueueOutgoingAsync(envelope);
        }
        
        _logger.LogInformation("Recovered {Count} messages from outbox for destination {Destination} while discarding {ExpiredCount} expired messages", good.Length, _sendingAgent.Destination, expiredMessages.Length);
    }
}

