using System.Data.Common;
using Marten.Internal;
using Marten.Internal.Operations;
using Marten.Services;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Storage;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace Wolverine.Marten.Persistence.Operations;

/// <summary>
///     GH-5665. Writes, or deletes, a saga's history rows in the Marten session's own transaction
/// </summary>
internal class StoreSagaHistory : global::Marten.Internal.Operations.IStorageOperation, NoDataReturnedCall
{
    private readonly PostgresqlMessageStore _store;
    private readonly SagaHistoryRecord? _record;
    private readonly (string SagaType, string SagaId, string? TenantId)? _delete;

    public StoreSagaHistory(PostgresqlMessageStore store, SagaHistoryRecord record)
    {
        _store = store;
        _record = record;
    }

    public StoreSagaHistory(PostgresqlMessageStore store, string sagaType, string sagaId, string? tenantId)
    {
        _store = store;
        _delete = (sagaType, sagaId, tenantId);
    }

    public void ConfigureCommand(Weasel.Postgresql.ICommandBuilder builder, IStorageSession session)
    {
        if (_record != null)
        {
            SagaHistorySql.AppendInsert(builder, _store.SagaHistoryFullName, _record,
                _store.SagaHistoryTimestamp(_record.RecordedAt));
        }
        else
        {
            var (sagaType, sagaId, tenantId) = _delete!.Value;
            SagaHistorySql.AppendDelete(builder, _store.SagaHistoryFullName, sagaType, sagaId, tenantId);
        }

        builder.Append(";");
    }

    public void Postprocess(DbDataReader reader, IList<Exception> exceptions)
    {
        // Nothing
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public OperationRole Role()
    {
        return OperationRole.Other;
    }

    public Type DocumentType => typeof(SagaHistoryRecord);
}
