namespace Wolverine.Persistence.Sagas;

public interface ISagaStorage<TSaga> where TSaga : Saga
{
    Task InsertAsync(TSaga saga, CancellationToken cancellationToken);
    Task UpdateAsync(TSaga saga, CancellationToken cancellationToken);
    Task DeleteAsync(TSaga saga, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ISagaStorage<TId, TSaga> : ISagaStorage<TSaga>, IAsyncDisposable where TSaga : Saga
{
    Task<TSaga?> LoadAsync(TId id, CancellationToken cancellationToken);
}