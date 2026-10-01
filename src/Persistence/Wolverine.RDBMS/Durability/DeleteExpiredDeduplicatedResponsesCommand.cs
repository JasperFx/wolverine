using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// GH-4742. The reaper for <c>[DeduplicatedWithResponse]</c> claims, as
/// <see cref="DeleteExpiredDeduplicationClaimsCommand" /> is for plain ones. It matters more here: every row
/// can carry a response body.
/// </summary>
internal class DeleteExpiredDeduplicatedResponsesCommand : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly ILogger _logger;

    public DeleteExpiredDeduplicatedResponsesCommand(IMessageDatabase database, ILogger logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        if (_database.HasDisposed || _database is not IReplayableDeduplicationStore { Enabled: true } store)
        {
            return AgentCommands.Empty;
        }

        try
        {
            var deleted = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Deleted {Count} expired deduplicated responses from database {Database}", deleted,
                    _database.Name);
            }
        }
        catch (Exception e)
        {
            // As the claims reaper: never take down the durability agent.
            _logger.LogError(e, "Error trying to delete expired deduplicated responses from database {Database}",
                _database.Name);
        }

        return AgentCommands.Empty;
    }
}
