using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Recurring;

/// <summary>
/// Cross-provider <see cref="IRecurringMessageStore" /> backed by the single
/// <c>wolverine_recurring_messages</c> table — one row per registered recurring schedule, keyed by
/// the schedule name.
///
/// <para>
/// Plain parameterized INSERT / UPDATE / DELETE only, no per-provider UPSERT or MERGE syntax, so
/// the same implementation serves PostgreSQL, SQL Server, MySQL and SQLite unchanged (the
/// <see cref="Deduplication.RdbmsDeduplicationStore" /> precedent). The upsert is
/// update-then-insert with the provider's duplicate-key classifier arbitrating the one real race
/// — the single cluster-wide agent writing a schedule's first row while an operator pauses it.
/// </para>
///
/// <para>
/// <see cref="PauseAsync" /> runs its two effects — marking the row paused and cancelling the
/// tracked pre-scheduled inbox envelopes — in ONE transaction, which is the point of the
/// extension living beside the inbox in the same database: nothing may fire in the gap between
/// the mark and the cancel, and a half-applied pause cannot survive a crash.
/// </para>
/// </summary>
internal sealed class RdbmsRecurringMessageStore : IRecurringMessageStore
{
    private readonly DbDataSource _dataSource;
    private readonly string _incomingTable;
    private readonly Func<Exception, bool> _isUniqueConstraintViolation;

    private readonly string _loadSql;
    private readonly string _loadAllSql;
    private readonly string _selectIdsSql;
    private readonly string _updatePublishSql;
    private readonly string _insertSql;
    private readonly string _markPausedSql;
    private readonly string _resumeSql;
    private readonly string _cancelScheduledEnvelopeSql;

    /// <param name="recurringTable">
    /// The fully rendered storage identifier for the recurring-messages table, as produced by
    /// <see cref="MessageDatabase{T}.QuotedTableNameFor" /> — schema-qualified on engines that
    /// have schemas, a prefixed single identifier on SQLite (GH-3943).
    /// </param>
    /// <param name="incomingTable">
    /// The rendered identifier for the incoming-envelopes table, for the eager cancel and the
    /// still-scheduled verification count.
    /// </param>
    public RdbmsRecurringMessageStore(
        DbDataSource dataSource,
        string recurringTable,
        string incomingTable,
        Func<Exception, bool> isUniqueConstraintViolation)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _incomingTable = incomingTable ?? throw new ArgumentNullException(nameof(incomingTable));
        _isUniqueConstraintViolation = isUniqueConstraintViolation
                                       ?? throw new ArgumentNullException(nameof(isUniqueConstraintViolation));

        var selectFields =
            $"{DatabaseConstants.ScheduleName}, {DatabaseConstants.CronExpression}, {DatabaseConstants.EnvelopeIds}, " +
            $"{DatabaseConstants.DeduplicationId}, {DatabaseConstants.NextOccurrence}, {DatabaseConstants.Paused}, " +
            $"{DatabaseConstants.PausedAt}, {DatabaseConstants.LastUpdated}";

        _loadSql =
            $"select {selectFields} from {recurringTable} where {DatabaseConstants.ScheduleName} = @name";

        _loadAllSql =
            $"select {selectFields} from {recurringTable} order by {DatabaseConstants.ScheduleName}";

        _selectIdsSql =
            $"select {DatabaseConstants.EnvelopeIds} from {recurringTable} where {DatabaseConstants.ScheduleName} = @name";

        // Preserves paused/paused_at on purpose: the agent's record of a publish is never
        // permission to un-pause.
        _updatePublishSql =
            $"update {recurringTable} set {DatabaseConstants.CronExpression} = @cron, " +
            $"{DatabaseConstants.EnvelopeIds} = @ids, {DatabaseConstants.DeduplicationId} = @dedup, " +
            $"{DatabaseConstants.NextOccurrence} = @next, {DatabaseConstants.LastUpdated} = @updated " +
            $"where {DatabaseConstants.ScheduleName} = @name";

        _insertSql =
            $"insert into {recurringTable} ({DatabaseConstants.ScheduleName}, {DatabaseConstants.CronExpression}, " +
            $"{DatabaseConstants.EnvelopeIds}, {DatabaseConstants.DeduplicationId}, {DatabaseConstants.NextOccurrence}, " +
            $"{DatabaseConstants.Paused}, {DatabaseConstants.PausedAt}, {DatabaseConstants.LastUpdated}) " +
            "values (@name, @cron, @ids, @dedup, @next, @paused, @pausedat, @updated)";

        // COALESCE keeps the original pause instant on a double-pause; ResumeAsync nulls it, so
        // the column doubles as the transition marker.
        _markPausedSql =
            $"update {recurringTable} set {DatabaseConstants.Paused} = @paused, " +
            $"{DatabaseConstants.PausedAt} = coalesce({DatabaseConstants.PausedAt}, @pausedat), " +
            $"{DatabaseConstants.EnvelopeIds} = @ids, {DatabaseConstants.NextOccurrence} = @next, " +
            $"{DatabaseConstants.LastUpdated} = @updated " +
            $"where {DatabaseConstants.ScheduleName} = @name";

        _resumeSql =
            $"update {recurringTable} set {DatabaseConstants.Paused} = @paused, " +
            $"{DatabaseConstants.PausedAt} = @pausedat, {DatabaseConstants.LastUpdated} = @updated " +
            $"where {DatabaseConstants.ScheduleName} = @name";

        // The same delete IScheduledMessages.CancelAsync issues — a scheduled envelope's
        // cancellation IS its removal from the inbox. The status predicate keeps a race with the
        // durability poller honest: an envelope already claimed for execution is not cancelled.
        _cancelScheduledEnvelopeSql =
            $"delete from {incomingTable} where {DatabaseConstants.Id} = @id " +
            $"and {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'";
    }

    public async Task RecordPublishedAsync(RecurringMessageRecord record, CancellationToken token = default)
    {
        if (record.Name.IsEmpty()) throw new ArgumentOutOfRangeException(nameof(record), "Name is required");

        if (await tryUpdatePublishAsync(record, token)) return;

        try
        {
            await using var insert = _dataSource.CreateCommand(_insertSql)
                .With("name", record.Name)
                .With("cron", record.CronExpression)
                .With("ids", joinIds(record.EnvelopeIds))
                .With("dedup", (object?)record.DeduplicationId ?? DBNull.Value)
                .With("next", (object?)record.NextOccurrence ?? DBNull.Value)
                .With("paused", false)
                .With("pausedat", DBNull.Value)
                .With("updated", record.LastUpdated);

            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        catch (Exception e) when (_isUniqueConstraintViolation(e))
        {
            // Lost the first-row race (an operator's pause upserted it between our update and
            // insert). The row exists now, so the update applies.
            await tryUpdatePublishAsync(record, token);
        }
    }

    private async Task<bool> tryUpdatePublishAsync(RecurringMessageRecord record, CancellationToken token)
    {
        await using var update = _dataSource.CreateCommand(_updatePublishSql)
            .With("cron", record.CronExpression)
            .With("ids", joinIds(record.EnvelopeIds))
            .With("dedup", (object?)record.DeduplicationId ?? DBNull.Value)
            .With("next", (object?)record.NextOccurrence ?? DBNull.Value)
            .With("updated", record.LastUpdated)
            .With("name", record.Name);

        return await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
    }

    public async Task<RecurringMessageRecord?> LoadAsync(string name, CancellationToken token = default)
    {
        await using var cmd = _dataSource.CreateCommand(_loadSql).With("name", name);
        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);

        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? await readRecordAsync(reader, token)
            : null;
    }

    public async Task<IReadOnlyList<RecurringMessageRecord>> LoadAllAsync(CancellationToken token = default)
    {
        var list = new List<RecurringMessageRecord>();

        await using var cmd = _dataSource.CreateCommand(_loadAllSql);
        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            list.Add(await readRecordAsync(reader, token));
        }

        return list;
    }

    public async Task<int> CountStillScheduledAsync(Guid[] envelopeIds, CancellationToken token = default)
    {
        if (envelopeIds.Length == 0) return 0;

        // An explicit parameter list rather than a provider array bind — N is the subscriber
        // count of one message type, effectively always a handful.
        var placeholders = string.Join(", ", envelopeIds.Select((_, i) => $"@id{i}"));
        var sql =
            $"select count(*) from {_incomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}' " +
            $"and {DatabaseConstants.Id} in ({placeholders})";

        await using var cmd = _dataSource.CreateCommand(sql);
        for (var i = 0; i < envelopeIds.Length; i++)
        {
            cmd.With($"id{i}", envelopeIds[i]);
        }

        var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    public async Task PauseAsync(string name, DateTimeOffset pausedAt, CancellationToken token = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false);

        try
        {
            // Read the tracked envelopes before the mark clears them off the row.
            Guid[] pending = [];
            var load = tx.CreateCommand(_selectIdsSql).With("name", name);
            await using (load)
            {
                var raw = await load.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (raw is string text)
                {
                    pending = parseIds(text);
                }
            }

            var mark = tx.CreateCommand(_markPausedSql)
                .With("paused", true)
                .With("pausedat", pausedAt)
                .With("ids", string.Empty)
                .With("next", DBNull.Value)
                .With("updated", pausedAt)
                .With("name", name);

            int marked;
            await using (mark)
            {
                marked = await mark.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            if (marked == 0)
            {
                // No row yet — the schedule has never published (or been paused). A paused-only
                // row makes pausing-before-the-first-publish work; the agent honours it and
                // never publishes. No unique-violation retry needed: the writes to this table
                // are this transaction and the single cluster-wide agent, and the agent's own
                // insert path retries into an update on exactly this collision.
                var insert = tx.CreateCommand(_insertSql)
                    .With("name", name)
                    .With("cron", string.Empty)
                    .With("ids", string.Empty)
                    .With("dedup", DBNull.Value)
                    .With("next", DBNull.Value)
                    .With("paused", true)
                    .With("pausedat", pausedAt)
                    .With("updated", pausedAt);

                await using (insert)
                {
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            // The eager cancel — in the SAME transaction as the mark, so nothing fires in the gap
            // and a crash cannot leave the schedule paused with its occurrence still live.
            foreach (var id in pending)
            {
                var cancel = tx.CreateCommand(_cancelScheduledEnvelopeSql).With("id", id);
                await using (cancel)
                {
                    await cancel.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ResumeAsync(string name, CancellationToken token = default)
    {
        await using var cmd = _dataSource.CreateCommand(_resumeSql)
            .With("paused", false)
            .With("pausedat", DBNull.Value)
            .With("updated", DateTimeOffset.UtcNow)
            .With("name", name);

        // UPDATE of a missing or already-running row affects 0 rows — resume is naturally
        // idempotent, and unknown-name validation belongs to the layer that knows the
        // registrations (IRecurringScheduleControl).
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static string joinIds(Guid[] ids)
    {
        return ids.Length == 0 ? string.Empty : string.Join(",", ids.Select(x => x.ToString("N")));
    }

    private static Guid[] parseIds(string text)
    {
        return text.IsEmpty()
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Guid.Parse).ToArray();
    }

    private static async Task<RecurringMessageRecord> readRecordAsync(DbDataReader reader, CancellationToken token)
    {
        return new RecurringMessageRecord
        {
            Name = await reader.GetFieldValueAsync<string>(0, token).ConfigureAwait(false),
            CronExpression = await reader.GetFieldValueAsync<string>(1, token).ConfigureAwait(false),
            EnvelopeIds = parseIds(await reader.GetFieldValueAsync<string>(2, token).ConfigureAwait(false)),
            DeduplicationId = await reader.IsDBNullAsync(3, token).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<string>(3, token).ConfigureAwait(false),
            NextOccurrence = await reader.IsDBNullAsync(4, token).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(4, token).ConfigureAwait(false),
            Paused = await reader.GetFieldValueAsync<bool>(5, token).ConfigureAwait(false),
            PausedAt = await reader.IsDBNullAsync(6, token).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(6, token).ConfigureAwait(false),
            LastUpdated = await reader.GetFieldValueAsync<DateTimeOffset>(7, token).ConfigureAwait(false)
        };
    }
}
