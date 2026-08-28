using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// GH-4180. The reaper for logical deduplication claims.
///
/// <para>
/// This is not optional housekeeping. <c>wolverine_deduplication</c> is append-only on the write
/// path — one row per deduplicated message, forever — so without a reaper, enabling the feature
/// trades duplicate work for a table that grows without bound and takes the primary key index with
/// it. The existing <c>DeleteExpiredHandledEnvelopesCommand</c> cannot cover it: that one deletes
/// from the inbox on <c>keep_until</c>, and these claims deliberately live in their own table with
/// their own, much longer, <see cref="DurabilitySettings.DeduplicationWindow" />.
/// </para>
///
/// <para>
/// Runs on its own timer in its own transaction, off the recovery loop — same isolation, and for the
/// same reason, as the handled-envelope cleanup (issue #3116): a large delete must not be able to
/// block inbox recovery.
/// </para>
///
/// <para>
/// Progress is reported rather than silent. A reaper that cannot say how much it removed turns an
/// unbounded table into an unbounded table nobody is watching, so every cycle that deletes anything
/// logs the count — a count that keeps climbing is the signal that the window is too long, the
/// cadence too slow, or the volume higher than the settings assume.
/// </para>
/// </summary>
internal class DeleteExpiredDeduplicationClaimsCommand : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly ILogger _logger;

    public DeleteExpiredDeduplicationClaimsCommand(IMessageDatabase database, ILogger logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        if (_database.HasDisposed) return AgentCommands.Empty;

        var now = DateTimeOffset.UtcNow;

        try
        {
            var deleted = await _database.Deduplication.DeleteExpiredAsync(now, cancellationToken)
                .ConfigureAwait(false);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Deleted {Count} expired logical deduplication claims from database {Database}",
                    deleted, _database.Name);
            }
        }
        catch (Exception e)
        {
            // Never let reaper failure take down the durability agent -- the claims are still correct,
            // they are just not being cleaned up, and the next cycle gets another go.
            _logger.LogError(e, "Error trying to delete expired logical deduplication claims from database {Database}",
                _database.Name);
        }

        return AgentCommands.Empty;
    }
}
