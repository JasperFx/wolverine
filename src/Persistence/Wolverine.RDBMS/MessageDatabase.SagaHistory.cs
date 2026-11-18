using System.Data.Common;
using Weasel.Core;
using Wolverine.Persistence.Sagas;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

/// <summary>
///     GH-5665. The SQL of the <c>wolverine_saga_history</c> table, written once for every relational message store and
///     for the document stores' outbox enlistments, which append it to their own command builders
/// </summary>
public static class SagaHistorySql
{
    /// <summary>Append the insert of one history row to any Weasel command builder</summary>
    public static void AppendInsert(ICommandBuilder builder, string tableName, SagaHistoryRecord record, object recordedAt)
        => appendInsert(builder.Append, p => builder.AppendParameter(p), tableName, record, recordedAt);

    /// <summary>Append the insert of one history row</summary>
    public static void AppendInsert(DbCommandBuilder builder, string tableName, SagaHistoryRecord record, object recordedAt)
        => appendInsert(builder.Append, p => builder.AppendParameter(p), tableName, record, recordedAt);

    /// <summary>Append the delete of one saga's history to any Weasel command builder</summary>
    public static void AppendDelete(ICommandBuilder builder, string tableName, string sagaType, string sagaId,
        string? tenantId)
        => appendDelete(builder.Append, p => builder.AppendParameter(p), tableName, sagaType, sagaId, tenantId);

    /// <summary>Append the delete of one saga's history, in one tenant when <paramref name="tenantId" /> is not null</summary>
    public static void AppendDelete(DbCommandBuilder builder, string tableName, string sagaType, string sagaId,
        string? tenantId)
        => appendDelete(builder.Append, p => builder.AppendParameter(p), tableName, sagaType, sagaId, tenantId);

    /// <param name="sql">Appends SQL text</param>
    /// <param name="parameter">Appends a parameter</param>
    /// <param name="tableName">The table's qualified name</param>
    /// <param name="record">The row</param>
    /// <param name="recordedAt">The <c>recorded_at</c> value, as this provider binds a timestamp</param>
    private static void appendInsert(Action<string> sql, Action<object> parameter, string tableName,
        SagaHistoryRecord record, object recordedAt)
    {
        sql($"insert into {tableName} ({DatabaseConstants.SagaHistoryFields}) values (");
        parameter(record.Id.ToString());
        sql(", ");
        parameter(record.SagaType);
        sql(", ");
        parameter(record.SagaId);
        sql(", ");
        parameter((object?)record.TenantId ?? DBNull.Value);
        sql(", ");
        parameter(record.MessageId.HasValue ? record.MessageId.Value.ToString() : DBNull.Value);
        sql(", ");
        parameter(record.MessageType);
        sql(", ");
        parameter(record.Handler);
        sql(", ");
        parameter(record.Outcome.ToString());
        sql(", ");
        parameter(record.CompletesSaga);
        sql(", ");
        parameter(recordedAt);
        sql(", ");
        parameter(record.SentMessagesJson());
        sql(")");
    }

    private static void appendDelete(Action<string> sql, Action<object> parameter, string tableName, string sagaType,
        string sagaId, string? tenantId)
    {
        sql($"delete from {tableName} where {DatabaseConstants.SagaType} = ");
        parameter(sagaType);
        sql($" and {DatabaseConstants.SagaId} = ");
        parameter(sagaId);
        AppendTenant(sql, parameter, tenantId);
    }

    internal static void AppendTenant(Action<string> sql, Action<object> parameter, string? tenantId)
    {
        if (tenantId == null) return;

        sql($" and {DatabaseConstants.SagaHistoryTenantId} = ");
        parameter(tenantId);
    }

    internal static void AppendTenant(DbCommandBuilder builder, string? tenantId)
        => AppendTenant(builder.Append, p => builder.AppendParameter(p), tenantId);
}

/// <summary>
///     GH-5665. A relational message store's saga history table, written in a transaction someone else owns: the saga
///     storage's, or a DbContext's
/// </summary>
public interface ISagaHistoryDatabase : ISagaHistoryStore
{
    /// <summary>Write one history row in an open transaction of this database</summary>
    Task WriteSagaHistoryAsync(DbTransaction tx, SagaHistoryRecord record, CancellationToken token);

    /// <summary>Delete one saga's history in an open transaction of this database</summary>
    Task DeleteSagaHistoryAsync(DbTransaction tx, string sagaType, string sagaId, string? tenantId,
        CancellationToken token);
}

public abstract partial class MessageDatabase<T> : ISagaHistoryDatabase
{
    /// <summary>GH-5665. The qualified name of the saga history table</summary>
    public string SagaHistoryFullName => QuotedTableNameFor(DatabaseConstants.SagaHistoryTable);

    /// <summary>GH-5665. A <c>recorded_at</c> value as this provider binds it</summary>
    public virtual object SagaHistoryTimestamp(DateTimeOffset value) => value.ToUniversalTime();

    /// <summary>GH-5665. Reads a <c>recorded_at</c> value back</summary>
    protected virtual DateTimeOffset readSagaHistoryTimestamp(DbDataReader reader, int ordinal)
        => reader.GetFieldValue<DateTimeOffset>(ordinal);

    /// <summary>GH-5665. The paging of a history read, after its <c>order by</c></summary>
    protected virtual void writeSagaHistoryPaging(DbCommandBuilder builder, int skip, int take)
    {
        builder.Append($" limit {take} offset {skip}");
    }

    /// <summary>GH-5665. Write one history row in an open transaction of this database</summary>
    public async Task WriteSagaHistoryAsync(DbTransaction tx, SagaHistoryRecord record, CancellationToken token)
    {
        var builder = ToCommandBuilder();
        SagaHistorySql.AppendInsert(builder, SagaHistoryFullName, record, SagaHistoryTimestamp(record.RecordedAt));
        await executeAsync(builder, tx, token).ConfigureAwait(false);
    }

    /// <summary>GH-5665. Delete one saga's history in an open transaction of this database</summary>
    public async Task DeleteSagaHistoryAsync(DbTransaction tx, string sagaType, string sagaId, string? tenantId,
        CancellationToken token)
    {
        var builder = ToCommandBuilder();
        SagaHistorySql.AppendDelete(builder, SagaHistoryFullName, sagaType, sagaId, tenantId);
        await executeAsync(builder, tx, token).ConfigureAwait(false);
    }

    private static async Task executeAsync(DbCommandBuilder builder, DbTransaction tx, CancellationToken token)
    {
        await using var command = builder.Compile();
        command.Connection = tx.Connection;
        command.Transaction = tx;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    async Task ISagaHistoryStore.RecordSagaHistoryAsync(SagaHistoryRecord record, CancellationToken token)
    {
        await using var conn = await DataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false);
        await WriteSagaHistoryAsync(tx, record, token).ConfigureAwait(false);
        await tx.CommitAsync(token).ConfigureAwait(false);
    }

    async Task ISagaHistoryStore.DeleteSagaHistoryAsync(string sagaType, string sagaId, string? tenantId,
        CancellationToken token)
    {
        await using var conn = await DataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false);
        await DeleteSagaHistoryAsync(tx, sagaType, sagaId, tenantId, token).ConfigureAwait(false);
        await tx.CommitAsync(token).ConfigureAwait(false);
    }

    async Task<SagaHistoryRows> ISagaHistoryStore.ReadSagaHistoryAsync(string sagaType, string sagaId,
        string? tenantId, int skip, int take, CancellationToken token)
    {
        await using var conn = await DataSource.OpenConnectionAsync(token).ConfigureAwait(false);

        var count = ToCommandBuilder();
        count.Append($"select count(*) from {SagaHistoryFullName} where {DatabaseConstants.SagaType} = ");
        count.AppendParameter(sagaType);
        count.Append($" and {DatabaseConstants.SagaId} = ");
        count.AppendParameter(sagaId);
        SagaHistorySql.AppendTenant(count, tenantId);

        long total;
        await using (var command = count.Compile())
        {
            command.Connection = conn;
            total = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
        }

        if (total == 0 || take <= 0) return new SagaHistoryRows([], total);

        var select = ToCommandBuilder();
        select.Append($"select {DatabaseConstants.SagaHistoryFields} from {SagaHistoryFullName} where {DatabaseConstants.SagaType} = ");
        select.AppendParameter(sagaType);
        select.Append($" and {DatabaseConstants.SagaId} = ");
        select.AppendParameter(sagaId);
        SagaHistorySql.AppendTenant(select, tenantId);
        select.Append($" order by {DatabaseConstants.RecordedAt}, {DatabaseConstants.Id}");
        writeSagaHistoryPaging(select, skip, take);

        var records = new List<SagaHistoryRecord>();
        await using (var command = select.Compile())
        {
            command.Connection = conn;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                records.Add(readSagaHistoryRecord(reader));
            }
        }

        return new SagaHistoryRows(records, total);
    }

    private SagaHistoryRecord readSagaHistoryRecord(DbDataReader reader)
    {
        string? text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal).ToString();

        return new SagaHistoryRecord
        {
            Id = Guid.Parse(text(0)!),
            SagaType = text(1)!,
            SagaId = text(2)!,
            TenantId = text(3),
            MessageId = text(4) is { } messageId ? Guid.Parse(messageId) : null,
            MessageType = text(5)!,
            Handler = text(6)!,
            Outcome = Enum.Parse<SagaHistoryOutcome>(text(7)!),
            CompletesSaga = Convert.ToBoolean(reader.GetValue(8)),
            RecordedAt = readSagaHistoryTimestamp(reader, 9),
            SentMessages = SagaHistoryRecord.ParseSentMessages(text(10))
        };
    }

    async Task<long> ISagaHistoryStore.DeleteHistoryOfSagasCompletedBeforeAsync(string sagaType,
        DateTimeOffset completedBefore, CancellationToken token)
    {
        // A saga is its id within its tenant, so a completed saga in one tenant leaves another tenant's saga of the same id
        // alone. The inner select is wrapped in a derived table because MySQL refuses a subquery over the table it
        // deletes from, and the key is built with two-argument concat() because that is the form every provider has
        var key = $"concat(concat({DatabaseConstants.SagaId}, '|'), coalesce({DatabaseConstants.SagaHistoryTenantId}, ''))";

        var builder = ToCommandBuilder();
        builder.Append($"delete from {SagaHistoryFullName} where {DatabaseConstants.SagaType} = ");
        builder.AppendParameter(sagaType);
        builder.Append($" and {key} in (select completed.saga_key from (select {key} as saga_key from {SagaHistoryFullName} where {DatabaseConstants.SagaType} = ");
        builder.AppendParameter(sagaType);
        builder.Append($" and {DatabaseConstants.CompletesSaga} = ");
        builder.AppendParameter(true);
        builder.Append($" and {DatabaseConstants.RecordedAt} < ");
        builder.AppendParameter(SagaHistoryTimestamp(completedBefore));
        builder.Append(") completed)");

        await using var conn = await DataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = builder.Compile();
        command.Connection = conn;
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
