using System.Data.Common;
using Wolverine.Persistence.Sagas;

namespace Wolverine.RDBMS.Sagas;

public class DatabaseSagaStorage<TId, TSaga> : ISagaStorage<TId, TSaga> where TSaga : Saga
{
    private readonly DbConnection _connection;
    private readonly IDatabaseSagaSchema<TId, TSaga> _schema;
    private DbTransaction? _tx;

    public DatabaseSagaStorage(DbConnection connection, DbTransaction tx, IDatabaseSagaSchema<TId, TSaga> schema)
    {
        _connection = connection;
        _tx = tx;
        _schema = schema;
    }

    public Task InsertAsync(TSaga saga, CancellationToken cancellationToken)
    {
        return _schema.InsertAsync(saga, _tx, cancellationToken);
    }

    public Task UpdateAsync(TSaga saga, CancellationToken cancellationToken)
    {
        return _schema.UpdateAsync(saga, _tx, cancellationToken);
    }

    public Task DeleteAsync(TSaga saga, CancellationToken cancellationToken)
    {
        return _schema.DeleteAsync(saga, _tx, cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _tx.CommitAsync(cancellationToken);
            _tx = null;
        }
        finally
        {
            await _connection.CloseAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            return _connection.DisposeAsync();
        }

        return new ValueTask();
    }

    public Task<TSaga?> LoadAsync(TId id, CancellationToken cancellationToken)
    {
        return _schema.LoadAsync(id, _tx, cancellationToken);
    }
}