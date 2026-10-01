using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Deduplication;

/// <summary>
/// GH-4742. Cross-provider <see cref="IReplayableDeduplicationStore" /> backed by the
/// <c>wolverine_deduplicated_responses</c> table. Plain INSERT / SELECT / UPDATE / DELETE, so the same SQL runs
/// on PostgreSQL, SQL Server, MySQL and SQLite, and claiming is arbitrated by the primary key exactly as in
/// <see cref="RdbmsDeduplicationStore" />.
/// </summary>
internal sealed class RdbmsReplayableDeduplicationStore : IReplayableDeduplicationStore
{
    private readonly DbDataSource _dataSource;
    private readonly Func<Exception, bool> _isUniqueConstraintViolation;
    private readonly Func<int, string?> _batchedDeleteExpiredSql;
    private readonly int _batchSize;

    private readonly string _insertSql;
    private readonly string _findSql;
    private readonly string _recordSql;
    private readonly string _deleteUnansweredSql;
    private readonly string _deleteExpiredSql;

    public RdbmsReplayableDeduplicationStore(DbDataSource dataSource, string table,
        Func<Exception, bool> isUniqueConstraintViolation, Func<int, string?> batchedDeleteExpiredSql, int batchSize)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _isUniqueConstraintViolation = isUniqueConstraintViolation
                                       ?? throw new ArgumentNullException(nameof(isUniqueConstraintViolation));
        _batchedDeleteExpiredSql = batchedDeleteExpiredSql
                                   ?? throw new ArgumentNullException(nameof(batchedDeleteExpiredSql));
        _batchSize = batchSize;

        _insertSql =
            $"insert into {table} ({DatabaseConstants.DeduplicationId}, {DatabaseConstants.Expires}, {DatabaseConstants.Fingerprint}) values (@id, @expires, @fingerprint)";
        _findSql =
            $"select {DatabaseConstants.Fingerprint}, {DatabaseConstants.ResponseStatusCode}, {DatabaseConstants.ResponseBody}, {DatabaseConstants.ResponseLocation} from {table} where {DatabaseConstants.DeduplicationId} = @id";
        _recordSql =
            $"update {table} set {DatabaseConstants.ResponseStatusCode} = @status, {DatabaseConstants.ResponseBody} = @body, {DatabaseConstants.ResponseLocation} = @location where {DatabaseConstants.DeduplicationId} = @id";
        _deleteUnansweredSql =
            $"delete from {table} where {DatabaseConstants.DeduplicationId} = @id and {DatabaseConstants.ResponseStatusCode} is null";
        _deleteExpiredSql = $"delete from {table} where {DatabaseConstants.Expires} <= @now";
    }

    public bool Enabled => true;

    public async Task<bool> TryClaimAsync(string deduplicationId, string fingerprint, DateTimeOffset expires,
        CancellationToken cancellation = default)
    {
        if (deduplicationId.IsEmpty()) throw new ArgumentNullException(nameof(deduplicationId));

        try
        {
            await using var cmd = _dataSource.CreateCommand(_insertSql)
                .With("id", deduplicationId)
                .With("expires", expires)
                .With("fingerprint", fingerprint);

            await cmd.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (_isUniqueConstraintViolation(e))
        {
            // Someone else holds this id. As RdbmsDeduplicationStore, an expired but unreaped claim lands here too.
            return false;
        }
    }

    public async Task<DeduplicatedResponseClaim?> FindAsync(string deduplicationId,
        CancellationToken cancellation = default)
    {
        await using var cmd = _dataSource.CreateCommand(_findSql).With("id", deduplicationId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellation).ConfigureAwait(false)) return null;

        var fingerprint = await reader.GetFieldValueAsync<string>(0, cancellation).ConfigureAwait(false);

        // No status: not answered yet.
        if (await reader.IsDBNullAsync(1, cancellation).ConfigureAwait(false))
        {
            return new DeduplicatedResponseClaim(fingerprint, null);
        }

        // Convert: SQLite returns a long.
        var status = Convert.ToInt32(await reader.GetFieldValueAsync<object>(1, cancellation).ConfigureAwait(false));

        return new DeduplicatedResponseClaim(fingerprint, new DeduplicatedResponse(status,
            await readStringAsync(reader, 2, cancellation).ConfigureAwait(false),
            await readStringAsync(reader, 3, cancellation).ConfigureAwait(false)));
    }

    public async Task RecordResponseAsync(string deduplicationId, DeduplicatedResponse response,
        CancellationToken cancellation = default)
    {
        await using var cmd = _dataSource.CreateCommand(_recordSql)
            .With("id", deduplicationId)
            .With("status", response.StatusCode)
            .With("body", (object?)response.Body ?? DBNull.Value)
            .With("location", (object?)response.Location ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
    }

    public async Task ReleaseUnansweredAsync(string deduplicationId, CancellationToken cancellation = default)
    {
        if (deduplicationId.IsEmpty()) return;

        await using var cmd = _dataSource.CreateCommand(_deleteUnansweredSql).With("id", deduplicationId);
        await cmd.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellation = default)
    {
        var batched = _batchedDeleteExpiredSql(_batchSize);

        if (batched.IsEmpty())
        {
            await using var single = _dataSource.CreateCommand(_deleteExpiredSql).With("now", utcNow);
            return await single.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
        }

        var total = 0;

        // As RdbmsDeduplicationStore: until a short batch proves nothing expired is left.
        while (!cancellation.IsCancellationRequested)
        {
            await using var cmd = _dataSource.CreateCommand(batched!).With("now", utcNow);
            var deleted = await cmd.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);

            total += deleted;

            if (deleted < _batchSize) break;
        }

        return total;
    }

    private static async Task<string?> readStringAsync(DbDataReader reader, int ordinal, CancellationToken token)
        => await reader.IsDBNullAsync(ordinal, token).ConfigureAwait(false)
            ? null
            : await reader.GetFieldValueAsync<string>(ordinal, token).ConfigureAwait(false);
}
