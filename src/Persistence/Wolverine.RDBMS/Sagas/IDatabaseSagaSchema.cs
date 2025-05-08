using System.Data.Common;
using Weasel.Core;

namespace Wolverine.RDBMS.Sagas;

// Strictly a marker type
public interface IDatabaseSagaSchema
{
    ISchemaObject Table { get; }
}

public interface IDatabaseSagaSchema<TSaga> : IDatabaseSagaSchema where TSaga : Saga
{
    Task InsertAsync(TSaga saga, DbTransaction transaction, CancellationToken cancellationToken);
    Task UpdateAsync(TSaga saga, DbTransaction transaction, CancellationToken cancellationToken);
    Task DeleteAsync(TSaga saga, DbTransaction transaction, CancellationToken cancellationToken);
}

public interface IDatabaseSagaSchema<TId, TSaga> : IDatabaseSagaSchema<TSaga> where TSaga : Saga
{
    Task<TSaga?> LoadAsync(TId id, DbTransaction tx, CancellationToken cancellationToken);
}