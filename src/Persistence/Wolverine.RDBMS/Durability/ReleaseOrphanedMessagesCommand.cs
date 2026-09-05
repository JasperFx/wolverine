using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// GH-3971: releases inbox/outbox messages owned by nodes that no longer exist.
///
/// <para>This replaces the two <c>ReleaseOrphanedMessages*Operation</c> statements that used to ride the
/// shared recovery batch. Three things were wrong with that arrangement at scale, and they compounded:</para>
///
/// <para><b>1. The predicate could not use an index.</b> The old statement was
/// <c>owner_id != 0 and owner_id not in (&lt;live nodes&gt;)</c>. The selective part is the <c>NOT IN</c>;
/// everything else matches essentially every row, because in a healthy fleet virtually every envelope is
/// owned by a <i>live</i> node. So it was a full scan of the whole inbox, per database, on every polling
/// cycle, finding nothing — and adding an index on <c>owner_id</c> did not change the plan. Measured by
/// the reporter on 82,586 rows: Seq Scan, 3,670 buffers, 12.2 ms, with or without the index. Working out
/// the dead owners first and asking for <c>owner_id in (&lt;dead&gt;)</c> makes the same work an Index
/// Scan at 2 buffers / 0.035 ms, and it degrades gracefully — nothing to do means nothing scanned.</para>
///
/// <para><b>2. The update was unbounded.</b> When a node does go away every envelope it owned qualifies
/// at once, in one statement. One shard in the reporting deployment: 82,520 rows in a single UPDATE,
/// 587,460 buffers, 368 ms — with 2 KB bodies, against production bodies averaging ~12 KB. Across shards
/// a single node loss orphaned ~910,000 rows. See <see cref="DurabilitySettings.OrphanedMessageReleaseBatchSize"/>.</para>
///
/// <para><b>3. It ran inside the shared recovery transaction</b> — the one #3116 deliberately moved the
/// expired-handled cleanup out of, for exactly this reason — so while it ran it blocked recovery work in
/// the same transaction and competed with ordinary inbox inserts. The reported symptom was
/// <c>TimeoutException: Timeout during writing attempt</c> on inbox inserts with RDS Performance Insights
/// showing every blocking session sitting on this one statement. Like
/// <see cref="DeleteExpiredHandledEnvelopesCommand"/>, this now runs on its own timer
/// (<see cref="DurabilitySettings.OrphanedMessageSweepPollingTime"/>) in its own transaction.</para>
/// </summary>
internal class ReleaseOrphanedMessagesCommand : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly DurabilitySettings _settings;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<int>? _activeNodeNumbers;
    private readonly int _highWaterMark;

    /// <param name="activeNodeNumbers">
    /// The live node numbers for an <c>Ancillary</c> database, which has no <c>wolverine_nodes</c> table of
    /// its own. Null for the <c>Main</c> database, which reads them from its own node table.
    /// </param>
    /// <param name="highWaterMark">
    /// GH-3850. The highest node number the ancillary list has ever been known to cover; owners above it
    /// registered after that list was taken, so it says nothing about them and they are left alone. Not
    /// used on the Main path, which needs no such guard — see <see cref="liveOwnersAsync"/>.
    /// </param>
    public ReleaseOrphanedMessagesCommand(IMessageDatabase database, DurabilitySettings settings, ILogger logger,
        IReadOnlyList<int>? activeNodeNumbers = null, int highWaterMark = 0)
    {
        _database = database;
        _settings = settings;
        _logger = logger;
        _activeNodeNumbers = activeNodeNumbers;
        _highWaterMark = highWaterMark;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var incoming = _database.DbObjectNameFor(DatabaseConstants.IncomingTable);
        var outgoing = _database.DbObjectNameFor(DatabaseConstants.OutgoingTable);

        // GH-4321: the inbox and outbox sweeps used to each open their own connection and each
        // re-read the identical live-node list — 2 connection acquisitions and 4 queries per
        // polling cycle in a healthy fleet where the sweep finds nothing. One connection and one
        // node read now serve both. The GH-3850 owners-then-nodes ordering survives intact:
        // BOTH owner reads happen before the single live-node read, so a node registering in the
        // gap is in the node list and cannot own anything either owner read saw.
        int[] deadIncoming;
        int[] deadOutgoing;

        try
        {
            await using var conn = await _database.DataSource.OpenConnectionAsync(cancellationToken);

            try
            {
                var incomingOwners = await fetchNodeNumbersAsync(
                    conn.CreateCommand(_database.DistinctOwnerIdsSql(incoming)), cancellationToken);
                var outgoingOwners = await fetchNodeNumbersAsync(
                    conn.CreateCommand(_database.DistinctOwnerIdsSql(outgoing)), cancellationToken);

                var live = await liveOwnersAsync(conn, cancellationToken);
                if (live == null)
                {
                    return AgentCommands.Empty;
                }

                deadIncoming = DetermineDeadOwners(incomingOwners, live, _highWaterMark);
                deadOutgoing = DetermineDeadOwners(outgoingOwners, live, _highWaterMark);
            }
            finally
            {
                await conn.CloseAsync();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error determining orphaned message owners in database {Database}", _database.Name);
            return AgentCommands.Empty;
        }

        await sweepAsync(incoming, deadIncoming, cancellationToken);
        await sweepAsync(outgoing, deadOutgoing, cancellationToken);

        return AgentCommands.Empty;
    }

    private async Task sweepAsync(DbObjectName table, int[] dead, CancellationToken cancellationToken)
    {
        // The steady state, and the whole point of the change: nothing owned by a departed node, so
        // nothing is read and nothing is written.
        if (dead.Length == 0) return;

        var deadOwnerList = dead.Select(x => x.ToString()).Join(", ");
        var released = 0;

        try
        {
            released = await releaseAsync(table, deadOwnerList, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error releasing orphaned messages in {Table} of database {Database}", table,
                _database.Name);
            return;
        }

        if (released > 0)
        {
            _logger.LogInformation(
                "Released {Count} orphaned messages in {Table} of database {Database} previously owned by departed nodes {Owners}",
                released, table, _database.Name, deadOwnerList);
        }
    }

    // ORDER MATTERS in ExecuteAsync above: the owners present in the tables are read BEFORE the live
    // node list, and the reads are not atomic with respect to a node registering in between.
    //
    //   owners-then-nodes: a node that registers in the gap cannot have written any envelope the
    //   owner reads saw, and IS in the node read, so it is never judged dead.
    //
    //   nodes-then-owners: that same node is absent from the node list and its brand-new envelopes
    //   ARE in the owner list -- so its live, in-flight work gets reset to owner_id = 0 and handed
    //   to somebody else. That is precisely the failure GH-3850 exists to prevent.

    /// <summary>
    /// Which of the owners actually present in the table have departed. Pure, and deliberately separated
    /// from the reads so the GH-3850 bound is testable without a database.
    /// </summary>
    /// <param name="highWaterMark">
    /// GH-3850, ancillary only (0 disables). The live-node list came from another database and is up to
    /// one polling interval old, so it cannot speak for a node that registered after it was taken. Node
    /// numbers are monotonic, so anything above the mark is newer than the list and is not ours to judge
    /// — releasing a <i>live</i> node's rows hands its in-flight work to somebody else.
    ///
    /// <para>Deliberately NOT <c>max(live)</c>, which looks equivalent and is not: when the
    /// highest-numbered node dies its number leaves the live list, the max drops below it, and its
    /// orphaned messages become permanently unreclaimable. See <c>ActiveNodeNumberCache.HighWaterMark</c>.</para>
    /// </param>
    internal static int[] DetermineDeadOwners(IEnumerable<int> ownersInTable, ICollection<int> liveOwners,
        int highWaterMark)
    {
        var dead = ownersInTable.Where(x => x != 0 && !liveOwners.Contains(x));

        if (highWaterMark > 0)
        {
            dead = dead.Where(x => x <= highWaterMark);
        }

        return dead.Distinct().OrderBy(x => x).ToArray();
    }

    /// <summary>
    /// The live node numbers, or null when they cannot be established — in which case the sweep does
    /// nothing rather than guessing, since an empty live set would condemn every row in the table.
    /// </summary>
    private async Task<HashSet<int>?> liveOwnersAsync(System.Data.Common.DbConnection conn,
        CancellationToken cancellationToken)
    {
        if (_activeNodeNumbers != null)
        {
            // Ancillary. An empty list means the lookup against the main database failed or found no
            // nodes at all; either way it is not evidence that every owner is dead.
            return _activeNodeNumbers.Count == 0 ? null : _activeNodeNumbers.ToHashSet();
        }

        // Main. The node table lives in this same database, so no high-water guard is needed: an owner_id
        // can only appear in the envelope table if that node's registration had already committed, so an
        // owner absent from this read has genuinely departed.
        var nodesTable = _database.DbObjectNameFor(DatabaseConstants.NodeTableName);
        var numbers = await fetchNodeNumbersAsync(
            conn.CreateCommand($"select {DatabaseConstants.NodeNumber} from {nodesTable}"), cancellationToken);

        return numbers.Count == 0 ? null : numbers.ToHashSet();
    }

    /// <summary>
    /// Read the first column as an <c>int</c>, tolerating whatever integral type the provider surfaces.
    /// </summary>
    /// <remarks>
    /// Oracle's <c>NUMBER</c> arrives from ODP.NET as an <c>Int64</c>, and <c>FetchListAsync&lt;int&gt;()</c>
    /// goes through <c>GetFieldValueAsync&lt;int&gt;()</c>, which throws <c>InvalidCastException</c> on it.
    /// Both reads in this sweep are of node numbers, and both are inside the try/catch that logs and
    /// returns — so on Oracle the whole sweep degraded to a silent no-op that released nothing, forever,
    /// while looking healthy apart from one log line per cycle. Convert instead of casting.
    /// </remarks>
    private static Task<IReadOnlyList<int>> fetchNodeNumbersAsync(System.Data.Common.DbCommand cmd,
        CancellationToken cancellationToken)
    {
        return cmd.FetchListAsync(async reader =>
            await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
                ? 0
                : Convert.ToInt32(await reader.GetFieldValueAsync<object>(0, cancellationToken)
                    .ConfigureAwait(false)), cancellationToken);
    }

    private async Task<int> releaseAsync(DbObjectName table, string deadOwnerList, CancellationToken cancellationToken)
    {
        var batchSize = _settings.OrphanedMessageReleaseBatchSize;
        var sql = _database.BatchedReleaseOwnershipSql(table, deadOwnerList, batchSize);

        await using var conn = await _database.DataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            if (sql.IsEmpty())
            {
                // This provider cannot bound the update; do it in one statement, but still on this
                // dedicated timer and transaction, off the shared recovery batch.
                return await conn
                    .CreateCommand(
                        $"update {table} set {DatabaseConstants.OwnerId} = 0 where {DatabaseConstants.OwnerId} in ({deadOwnerList})")
                    .ExecuteNonQueryAsync(cancellationToken);
            }

            var total = 0;
            for (var i = 0; i < _settings.OrphanedMessageReleaseMaxBatchesPerCycle; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var released = await conn.CreateCommand(sql).ExecuteNonQueryAsync(cancellationToken);
                total += released;

                // A short batch means everything currently orphaned has been released
                if (released < batchSize) break;
            }

            return total;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
