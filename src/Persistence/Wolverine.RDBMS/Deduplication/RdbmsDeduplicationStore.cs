using System.Data;
using System.Data.Common;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weasel.Core;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Deduplication;

/// <summary>
/// GH-4180. Cross-provider <see cref="IDeduplicationStore" /> backed by a single deduplication table.
///
/// <para>
/// Either table has two or three columns — the logical id, an expiry, and on the hashed table a binary
/// hash of the id — so every operation is a plain INSERT / DELETE with no per-provider UPSERT or MERGE
/// syntax. The same shape works on PostgreSQL, SQL Server, MySQL and SQLite unchanged. Oracle gets
/// nothing — <c>OracleMessageStore</c> does not derive from <see cref="MessageDatabase{T}" /> and
/// supplies no deduplication store of its own, so it keeps <see cref="NullDeduplicationStore" />.
/// </para>
///
/// <para>
/// GH-4757. <b>The mode decides which table, and therefore which column arbitrates.</b> Under
/// <see cref="MessageDeduplicationMode.CompareByString" /> this runs against
/// <c>wolverine_deduplication</c> and keys off <c>deduplication_id</c>, exactly as it always has; under
/// <see cref="MessageDeduplicationMode.CompareByHash" /> it runs against
/// <c>wolverine_deduplication_hashed</c> and keys off the binary <c>deduplication_hash</c>, with the
/// readable id riding along in a plain column. The caller passes the already-resolved table name, so
/// the only branch here is which columns to write. Both modes are an INSERT arbitrated by the primary
/// key — see below — because that property is the feature, not an implementation detail of either.
/// </para>
///
/// <para>
/// <b>Claiming is an INSERT, never a SELECT-then-INSERT.</b> That is not an optimisation, it is the
/// feature: the duplicates this exists to stop are concurrent (an operator double-clicking, two
/// nodes replaying the same schedule), and a check-then-act would let both through while passing
/// every single-threaded test written against it. The database's own unique constraint is the only
/// arbiter that holds across nodes. This mirrors <c>RdbmsListenerStore</c>'s try-insert /
/// classify-the-violation pattern, reusing <see cref="MessageDatabase{T}" />'s existing per-provider
/// duplicate-key classifier rather than growing a second one.
/// </para>
///
/// <para>
/// D1. <b>The claim records the envelope that took it.</b> This store's claim is committed before the handler
/// runs and only the compensating release gives it back, so a node that dies mid-handler strands it. Through
/// <see cref="IEnvelopeAwareDeduplicationStore" /> a copy of that same envelope -- the one inbox recovery brings
/// back -- takes the claim over with a conditional UPDATE keyed on the id AND the envelope id, while any other
/// envelope is still refused. The envelope column is nullable and additive, and a row without one (the
/// envelope-less claim, or a claim written before the column existed) can never be taken over.
/// </para>
///
/// <para>
/// D1 follow-up. <b>The column is detected, not assumed.</b> An application on <c>AutoCreate.None</c> that
/// upgrades without migrating still has the table without <c>envelope_id</c>, and an INSERT naming it would fail
/// every <c>[Deduplicated]</c> handler. So the first envelope-aware claim checks, once per store instance, whether
/// the column is there. Without it, the envelope-aware claim degrades to the original envelope-less claim, with
/// its original SQL, and one warning is logged. The answer is cached for the life of the process: after a manual
/// migration, a restart picks the column up.
/// </para>
/// </summary>
internal sealed class RdbmsDeduplicationStore : IDeduplicationStore, ITransactionalDeduplicationStore,
    IEnvelopeAwareDeduplicationStore
{
    private readonly DbDataSource _dataSource;
    private readonly Func<Exception, bool> _isUniqueConstraintViolation;
    private readonly string _insertSql;
    private readonly string _insertForEnvelopeSql;
    private readonly string _takeOverSql;
    private readonly string _probeEnvelopeColumnSql;
    private readonly string _probeTableSql;
    private readonly string _table;
    private readonly ILogger _logger;

    // 0 = not checked yet, 1 = envelope_id present, 2 = absent. Checked once, then a plain read on every claim.
    private int _envelopeColumnState;
    private readonly string _insertInTransactionSql;
    private readonly bool _insertInTransactionReportsConflicts;
    private readonly string _deleteSql;
    private readonly string _deleteExpiredSql;
    private readonly Func<int, string?> _batchedDeleteExpiredSql;
    private readonly int _batchSize;
    private readonly bool _compareByHash;

    /// <param name="table">
    /// The fully rendered storage identifier for the deduplication table, as produced by
    /// <see cref="MessageDatabase{T}.QuotedTableNameFor" /> — schema-qualified on engines that have
    /// schemas, and a prefixed single identifier on SQLite (GH-3943).
    /// </param>
    /// <param name="batchedDeleteExpiredSql">
    /// Provider hook returning a BOUNDED delete for expired claims, or null when the engine cannot
    /// express one — the same shape as
    /// <see cref="IMessageDatabase.BatchedDeleteExpiredHandledEnvelopesSql" />. Bounding matters here:
    /// a busy chain under a 24-hour window accumulates a day's worth of rows, and reaping them in one
    /// unbounded statement is exactly the long-lock problem that moved the handled-envelope cleanup
    /// onto its own timer in the first place (issue #3116).
    /// </param>
    /// <param name="mode">
    /// GH-4757. Which columns to write, matching the table <paramref name="table" /> names: the hashed
    /// table under <see cref="MessageDeduplicationMode.CompareByHash" />, the original otherwise. Never
    /// <see cref="MessageDeduplicationMode.None" /> — the caller only builds a real store at all when
    /// deduplication is on.
    /// </param>
    /// <param name="conflictClause">
    /// GH-5108. A clause appended to the claim INSERT when it runs inside a caller's transaction, making a
    /// conflicting id insert nothing instead of raising -- PostgreSQL's <c>on conflict do nothing</c>. Matters
    /// there because a failed statement aborts the whole transaction. Null on engines where a duplicate-key
    /// error leaves the transaction usable; the violation is classified exactly as on the other path.
    /// </param>
    public RdbmsDeduplicationStore(
        DbDataSource dataSource,
        string table,
        Func<Exception, bool> isUniqueConstraintViolation,
        Func<int, string?> batchedDeleteExpiredSql,
        int batchSize,
        MessageDeduplicationMode mode,
        string? conflictClause = null,
        ILogger? logger = null)
    {
        _table = table;
        _logger = logger ?? NullLogger.Instance;
        _probeEnvelopeColumnSql = $"select {DatabaseConstants.DeduplicationEnvelopeId} from {table} where 1 = 0";
        _probeTableSql = $"select 1 from {table} where 1 = 0";

        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _isUniqueConstraintViolation = isUniqueConstraintViolation
                                       ?? throw new ArgumentNullException(nameof(isUniqueConstraintViolation));
        _batchedDeleteExpiredSql = batchedDeleteExpiredSql
                                   ?? throw new ArgumentNullException(nameof(batchedDeleteExpiredSql));
        _batchSize = batchSize;
        _compareByHash = mode == MessageDeduplicationMode.CompareByHash;

        if (_compareByHash)
        {
            _insertSql =
                $"insert into {table} ({DatabaseConstants.DeduplicationHash}, {DatabaseConstants.DeduplicationId}, {DatabaseConstants.Expires}) values (@hash, @id, @expires)";
            _insertForEnvelopeSql =
                $"insert into {table} ({DatabaseConstants.DeduplicationHash}, {DatabaseConstants.DeduplicationId}, {DatabaseConstants.Expires}, {DatabaseConstants.DeduplicationEnvelopeId}) values (@hash, @id, @expires, @envelope)";
            _takeOverSql =
                $"update {table} set {DatabaseConstants.Expires} = @expires where {DatabaseConstants.DeduplicationHash} = @hash and {DatabaseConstants.DeduplicationEnvelopeId} = @envelope";
            _deleteSql = $"delete from {table} where {DatabaseConstants.DeduplicationHash} = @hash";
        }
        else
        {
            _insertSql =
                $"insert into {table} ({DatabaseConstants.DeduplicationId}, {DatabaseConstants.Expires}) values (@id, @expires)";
            _insertForEnvelopeSql =
                $"insert into {table} ({DatabaseConstants.DeduplicationId}, {DatabaseConstants.Expires}, {DatabaseConstants.DeduplicationEnvelopeId}) values (@id, @expires, @envelope)";
            _takeOverSql =
                $"update {table} set {DatabaseConstants.Expires} = @expires where {DatabaseConstants.DeduplicationId} = @id and {DatabaseConstants.DeduplicationEnvelopeId} = @envelope";
            _deleteSql = $"delete from {table} where {DatabaseConstants.DeduplicationId} = @id";
        }

        _deleteExpiredSql = $"delete from {table} where {DatabaseConstants.Expires} <= @now";

        _insertInTransactionReportsConflicts = conflictClause.IsNotEmpty();
        _insertInTransactionSql = _insertInTransactionReportsConflicts ? $"{_insertSql} {conflictClause}" : _insertSql;
    }

    public async Task<bool> TryClaimAsync(string deduplicationId, DateTimeOffset expires,
        CancellationToken cancellation = default)
    {
        if (deduplicationId.IsEmpty()) throw new ArgumentNullException(nameof(deduplicationId));

        try
        {
            await using var cmd = _dataSource.CreateCommand(_insertSql)
                .With("id", deduplicationId)
                .With("expires", expires);

            // DbType.Binary explicitly rather than letting each driver infer: Npgsql, SqlClient,
            // MySqlConnector and Microsoft.Data.Sqlite all accept a byte[], and all four infer something
            // slightly different for it.
            if (_compareByHash) cmd.With("hash", DeduplicationHash.For(deduplicationId), DbType.Binary);

            await cmd.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (_isUniqueConstraintViolation(e))
        {
            // Someone else holds this id. That is the answer, not an error.
            //
            // Note that an EXPIRED row also lands here: the reaper deletes on its own cadence, so a
            // claim can outlive its expiry by up to DeduplicationCleanupPollingTime. Treating that as
            // still-claimed is the conservative reading and errs toward refusing work that was already
            // done, which is the direction this feature exists to err in.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(string deduplicationId, Guid envelopeId, DateTimeOffset expires,
        CancellationToken cancellation = default)
    {
        if (deduplicationId.IsEmpty()) throw new ArgumentNullException(nameof(deduplicationId));

        // A table that predates envelope_id gets exactly the original claim, so an unmigrated database keeps working
        if (!await hasEnvelopeColumnAsync(cancellation).ConfigureAwait(false))
        {
            return await TryClaimAsync(deduplicationId, expires, cancellation).ConfigureAwait(false);
        }

        try
        {
            await using var cmd = _dataSource.CreateCommand(_insertForEnvelopeSql)
                .With("id", deduplicationId)
                .With("expires", expires)
                .With("envelope", envelopeId);

            if (_compareByHash) cmd.With("hash", DeduplicationHash.For(deduplicationId), DbType.Binary);

            await cmd.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (_isUniqueConstraintViolation(e))
        {
            // Held already. Ours to take over only if it was THIS envelope that took it, which is what a node dying
            // mid-handler leaves behind for inbox recovery to bring back. One conditional UPDATE, so two envelopes
            // can never both come away holding it.
        }

        await using var takeOver = _dataSource.CreateCommand(_takeOverSql)
            .With("expires", expires)
            .With("envelope", envelopeId);

        if (_compareByHash)
        {
            takeOver.With("hash", DeduplicationHash.For(deduplicationId), DbType.Binary);
        }
        else
        {
            takeOver.With("id", deduplicationId);
        }

        return await takeOver.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false) > 0;
    }

    private async ValueTask<bool> hasEnvelopeColumnAsync(CancellationToken cancellation)
    {
        var state = Volatile.Read(ref _envelopeColumnState);
        if (state != 0) return state == 1;

        // Two concurrent first claims may both probe. Harmless: they reach the same answer.
        var present = await probeEnvelopeColumnAsync(cancellation).ConfigureAwait(false);

        if (Interlocked.CompareExchange(ref _envelopeColumnState, present ? 1 : 2, 0) == 0 && !present)
        {
            // DRAFT: for Jeremy's review
            _logger.LogWarning(
                "The logical deduplication table {Table} predates the {Column} column, so [Deduplicated] claims cannot record the envelope that took them. Claims still work as they did before, but a claim stranded by a node that dies mid-handler will refuse the recovered message until it expires. Apply Wolverine's schema migration to add the column, then restart, to get crash-safe claims",
                _table, DatabaseConstants.DeduplicationEnvelopeId);
        }

        return present;
    }

    private async Task<bool> probeEnvelopeColumnAsync(CancellationToken cancellation)
    {
        try
        {
            await using var probe = _dataSource.CreateCommand(_probeEnvelopeColumnSql);
            await using var reader = await probe.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            // Either the column is missing, or the database could not be reached at all. Only the first is an
            // answer worth caching, so ask a question that fails only for the second: if this throws, it throws.
            await using var table = _dataSource.CreateCommand(_probeTableSql);
            await using var reader = await table.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(DbConnection connection, DbTransaction? transaction,
        string deduplicationId, DateTimeOffset expires, CancellationToken cancellation = default)
    {
        if (deduplicationId.IsEmpty()) throw new ArgumentNullException(nameof(deduplicationId));

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = _insertInTransactionSql;
        cmd.With("id", deduplicationId).With("expires", expires);
        if (_compareByHash) cmd.With("hash", DeduplicationHash.For(deduplicationId), DbType.Binary);

        try
        {
            var inserted = await cmd.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);

            // With a conflict clause a duplicate is zero rows rather than an exception
            return !_insertInTransactionReportsConflicts || inserted > 0;
        }
        catch (Exception e) when (_isUniqueConstraintViolation(e))
        {
            return false;
        }
    }

    public async Task ReleaseAsync(string deduplicationId, CancellationToken cancellation = default)
    {
        if (deduplicationId.IsEmpty()) return;

        await using var cmd = _compareByHash
            ? _dataSource.CreateCommand(_deleteSql)
                .With("hash", DeduplicationHash.For(deduplicationId), DbType.Binary)
            : _dataSource.CreateCommand(_deleteSql).With("id", deduplicationId);

        // DELETE is naturally idempotent -- releasing an unclaimed id affects 0 rows without raising.
        await cmd.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellation = default)
    {
        var batched = _batchedDeleteExpiredSql(_batchSize);

        if (batched.IsEmpty())
        {
            // This provider cannot bound the delete. Still correct, just one statement.
            await using var single = _dataSource.CreateCommand(_deleteExpiredSql).With("now", utcNow);
            return await single.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
        }

        var total = 0;

        // Bounded by the caller's cadence rather than a max-batches cap: the reaper owns its own timer,
        // so the natural stopping point is "nothing expired is left", and a short batch proves it.
        while (!cancellation.IsCancellationRequested)
        {
            await using var cmd = _dataSource.CreateCommand(batched!).With("now", utcNow);
            var deleted = await cmd.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);

            total += deleted;

            if (deleted < _batchSize) break;
        }

        return total;
    }
}
