using Wolverine.Persistence;

namespace Wolverine.Redis.Internal;

/// <summary>
/// What the generated code calls. Public because generated code lives in another assembly.
/// </summary>
public static class RedisStorageActionApplier
{
    public static Task<T?> LoadAsync<T>(IRedisDocumentSession session, object id, string? tenantId,
        CancellationToken token) where T : class
    {
        return session.LoadAsync<T>(id, tenantId, token);
    }

    public static Task<RedisSagaState<T>> LoadSagaAsync<T>(IRedisDocumentSession session, object id,
        string? tenantId, CancellationToken token) where T : class
    {
        return session.LoadSagaAsync<T>(id, tenantId, token);
    }

    public static Task InsertSagaAsync<T>(IRedisDocumentSession session, T saga, string? tenantId,
        CancellationToken token) where T : class
    {
        return session.InsertSagaAsync(saga, tenantId, token);
    }

    public static Task UpdateSagaAsync<T>(IRedisDocumentSession session, T saga, string? version,
        string? tenantId, CancellationToken token) where T : class
    {
        return session.UpdateSagaAsync(saga, version, tenantId, token);
    }

    public static Task DeleteSagaAsync<T>(IRedisDocumentSession session, object id, string? version,
        string? tenantId, CancellationToken token) where T : class
    {
        return session.DeleteSagaAsync<T>(id, version, tenantId, token);
    }

    /// <summary>
    /// Carry out one <see cref="IStorageAction{T}" /> — the <c>Storage.Store()</c> / <c>Insert()</c> /
    /// <c>Update()</c> / <c>Delete()</c> return values, and every element of a <c>UnitOfWork&lt;T&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Insert, Update and Store all become the same write. Redis has no insert-versus-update for a plain
    /// key, and a storage action carries no revision to compare against — the handler produced it out of
    /// an entity, not out of a read Wolverine tracked. These are last-write-wins by design; the
    /// compare-and-swap lives on the saga chain, which does have the revision it read.
    /// </remarks>
    public static Task ApplyAction<T>(IRedisDocumentSession session, IStorageAction<T> action, string? tenantId,
        CancellationToken token) where T : class
    {
        if (action.Entity == null)
        {
            return Task.CompletedTask;
        }

        return action.Action switch
        {
            StorageAction.Delete => session.DeleteAsync(action.Entity, tenantId, token),
            StorageAction.Insert or StorageAction.Store or StorageAction.Update =>
                session.StoreAsync(action.Entity, tenantId, token),
            _ => Task.CompletedTask
        };
    }

    public static Task StoreAsync<T>(IRedisDocumentSession session, T document, string? tenantId,
        CancellationToken token) where T : class
    {
        return document == null ? Task.CompletedTask : session.StoreAsync(document, tenantId, token);
    }

    public static Task DeleteAsync<T>(IRedisDocumentSession session, T document, string? tenantId,
        CancellationToken token) where T : class
    {
        return document == null ? Task.CompletedTask : session.DeleteAsync(document, tenantId, token);
    }
}
