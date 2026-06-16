using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// Background cleanup of expired, successfully handled incoming envelopes. Unlike the rest of the
/// recovery loop, this runs on its own timer in a dedicated transaction and, for providers that
/// support it, deletes in bounded batches so locks are held only briefly. This keeps the cleanup
/// from blocking live inbox traffic and recovery work under heavy load (see issue #3116).
/// </summary>
internal class DeleteExpiredHandledEnvelopesCommand : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly DurabilitySettings _settings;
    private readonly ILogger _logger;

    public DeleteExpiredHandledEnvelopesCommand(IMessageDatabase database, DurabilitySettings settings, ILogger logger)
    {
        _database = database;
        _settings = settings;
        _logger = logger;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var batchSize = _settings.HandledMessageCleanupBatchSize;
        var sql = _database.BatchedDeleteExpiredHandledEnvelopesSql(batchSize);

        if (string.IsNullOrEmpty(sql))
        {
            // This provider can't bound the delete; fall back to a single unbounded delete, but
            // still on this dedicated timer and transaction, off the main recovery loop.
            var incomingTable = new DbObjectName(_database.SchemaName, DatabaseConstants.IncomingTable);
            var fallback = new DatabaseOperationBatch(_database,
                [new DeleteExpiredEnvelopesOperation(incomingTable, now)]);
            return await fallback.ExecuteAsync(runtime, cancellationToken);
        }

        var total = 0;
        for (var i = 0; i < _settings.HandledMessageCleanupMaxBatchesPerCycle; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await using var conn = await _database.DataSource.OpenConnectionAsync(cancellationToken);

            int deleted;
            try
            {
                deleted = await conn.CreateCommand(sql)
                    .With("now", now)
                    .ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await conn.CloseAsync();
            }

            total += deleted;

            // A short batch means we've drained everything that's currently expired
            if (deleted < batchSize) break;
        }

        if (total > 0)
        {
            _logger.LogDebug("Deleted {Count} expired, handled incoming envelopes from database {Database}",
                total, _database.Name);
        }

        return AgentCommands.Empty;
    }
}
