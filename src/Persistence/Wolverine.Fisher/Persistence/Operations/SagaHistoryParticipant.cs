using Microsoft.Data.Sqlite;
using Fisher;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;

namespace Wolverine.Fisher.Persistence.Operations;

/// <summary>
///     GH-5665. Writes, or deletes, a saga's history rows in the Fisher session's own transaction
/// </summary>
internal class SagaHistoryParticipant : ITransactionParticipant
{
    private readonly ISagaHistoryDatabase _store;
    private readonly Func<ISagaHistoryDatabase, SqliteTransaction, CancellationToken, Task> _write;

    public SagaHistoryParticipant(ISagaHistoryDatabase store,
        Func<ISagaHistoryDatabase, SqliteTransaction, CancellationToken, Task> write)
    {
        _store = store;
        _write = write;
    }

    public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
        => _write(_store, transaction, token);

    public static SagaHistoryParticipant Insert(ISagaHistoryDatabase store, SagaHistoryRecord record)
        => new(store, (s, tx, token) => s.WriteSagaHistoryAsync(tx, record, token));

    public static SagaHistoryParticipant Delete(ISagaHistoryDatabase store, string sagaType, string sagaId,
        string? tenantId)
        => new(store, (s, tx, token) => s.DeleteSagaHistoryAsync(tx, sagaType, sagaId, tenantId, token));
}
