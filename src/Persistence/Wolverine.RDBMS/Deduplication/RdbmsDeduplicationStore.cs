using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Deduplication;

/// <summary>
/// GH-4180. Cross-provider <see cref="IDeduplicationStore" /> backed by the single
/// <c>wolverine_deduplication</c> table.
///
/// <para>
/// The table has exactly two columns — the logical id, which is also the primary key, and an
/// expiry — so every operation is a plain INSERT / DELETE with no per-provider UPSERT or MERGE
/// syntax. The same shape works on PostgreSQL, SQL Server, MySQL and SQLite unchanged; Oracle
/// supplies its own because <c>OracleMessageStore</c> does not derive from
/// <see cref="MessageDatabase{T}" />.
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
/// </summary>
internal sealed class RdbmsDeduplicationStore : IDeduplicationStore
{
    private readonly DbDataSource _dataSource;
    private readonly Func<Exception, bool> _isUniqueConstraintViolation;
    private readonly string _insertSql;
    private readonly string _deleteSql;
    private readonly string _deleteExpiredSql;
    private readonly Func<int, string?> _batchedDeleteExpiredSql;
    private readonly int _batchSize;

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
    public RdbmsDeduplicationStore(
        DbDataSource dataSource,
        string table,
        Func<Exception, bool> isUniqueConstraintViolation,
        Func<int, string?> batchedDeleteExpiredSql,
        int batchSize)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _isUniqueConstraintViolation = isUniqueConstraintViolation
                                       ?? throw new ArgumentNullException(nameof(isUniqueConstraintViolation));
        _batchedDeleteExpiredSql = batchedDeleteExpiredSql
                                   ?? throw new ArgumentNullException(nameof(batchedDeleteExpiredSql));
        _batchSize = batchSize;

        _insertSql =
            $"insert into {table} ({DatabaseConstants.DeduplicationId}, {DatabaseConstants.Expires}) values (@id, @expires)";
        _deleteSql = $"delete from {table} where {DatabaseConstants.DeduplicationId} = @id";
        _deleteExpiredSql = $"delete from {table} where {DatabaseConstants.Expires} <= @now";
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

    public async Task ReleaseAsync(string deduplicationId, CancellationToken cancellation = default)
    {
        if (deduplicationId.IsEmpty()) return;

        await using var cmd = _dataSource.CreateCommand(_deleteSql).With("id", deduplicationId);

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
