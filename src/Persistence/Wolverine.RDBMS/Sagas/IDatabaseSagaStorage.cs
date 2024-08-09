using System.Data.Common;
using System.Reflection;

namespace Wolverine.RDBMS.Sagas;

public interface IDatabaseSagaStorage
{
    Task InsertAsync<T>(T saga, DbTransaction transaction, CancellationToken cancellationToken) where T : Saga;
    Task UpdateAsync<T>(T saga, DbTransaction transaction, CancellationToken cancellationToken) where T : Saga;
    Task DeleteAsync<T>(T saga, DbTransaction transaction, CancellationToken cancellationToken) where T : Saga;
    Task<T?> LoadAsync<T, TId>(TId id, DbTransaction tx, CancellationToken cancellationToken) where T : Saga;
}