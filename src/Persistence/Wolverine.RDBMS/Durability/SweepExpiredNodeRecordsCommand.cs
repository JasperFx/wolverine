using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// GH-5498: implemented by a store's <see cref="Wolverine.Persistence.Durability.INodeAgentPersistence"/> when
/// the generic age sweep (<see cref="DeleteOldNodeEventRecords"/>, one <c>delete ... where timestamp &lt; @cutoff</c>
/// inside a <c>DatabaseOperationBatch</c> transaction) is not safe on that engine and the store runs its own.
///
/// MySQL is the reason: under InnoDB's default <c>REPEATABLE READ</c> the one-statement sweep locks rows in an
/// order that deadlocks against the row trim and the node-record inserts, and the safe form needs two statements,
/// a plain non-locking read of the upper id bound and then a primary-key range delete.
/// </summary>
public interface ISweepExpiredNodeRecords
{
    /// <summary>
    /// Delete every node record older than <paramref name="cutoff"/>
    /// </summary>
    Task DeleteExpiredNodeRecordsAsync(DateTimeOffset cutoff, CancellationToken token);
}

/// <summary>
/// GH-5498: the age sweep for a store that implements <see cref="ISweepExpiredNodeRecords"/>. Mirrors
/// <see cref="TrimNodeRecordsCommand"/>: housekeeping logs its failures and never faults the durability agent.
/// </summary>
internal class SweepExpiredNodeRecordsCommand : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly ISweepExpiredNodeRecords _sweep;
    private readonly DurabilitySettings _settings;
    private readonly ILogger _logger;

    public SweepExpiredNodeRecordsCommand(IMessageDatabase database, ISweepExpiredNodeRecords sweep,
        DurabilitySettings settings, ILogger logger)
    {
        _database = database;
        _sweep = sweep;
        _settings = settings;
        _logger = logger;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(_settings.NodeEventRecordExpirationTime);

        try
        {
            await _sweep.DeleteExpiredNodeRecordsAsync(cutoff, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Orderly shutdown
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to delete node records older than {Cutoff} in database {Database}",
                cutoff, _database.Name);
        }

        return AgentCommands.Empty;
    }
}
