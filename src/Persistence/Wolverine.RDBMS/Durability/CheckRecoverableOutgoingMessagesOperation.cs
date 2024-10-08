using System.Data.Common;
using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports.Sending;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class CheckRecoverableOutgoingMessagesOperation : IDatabaseOperation
{
    private readonly IMessageDatabase _database;
    private readonly ILogger _logger;
    private readonly IWolverineRuntime _runtime;
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
        builder.Append(
            $"select distinct destination from {_database.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = 0;");
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
                _logger.LogInformation("Found recoverable outgoing messages in the outbox for {Destination}",
                    destination);
                yield return new RecoverOutgoingMessagesCommand(sendingAgent, _database, _logger);
            }
        }
    }
}