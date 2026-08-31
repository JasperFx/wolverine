namespace Wolverine.Redis;

/// <summary>
/// One saga as it was read: the deserialized state, plus the revision the compare-and-swap on the next
/// write has to match.
/// </summary>
public readonly struct RedisSagaState<T> where T : class
{
    public RedisSagaState(T? saga, string? version)
    {
        Saga = saga;
        Version = version;
    }

    /// <summary>Null when there is no saga at that key.</summary>
    public T? Saga { get; }

    /// <summary>Null when there was no saga to read a revision from.</summary>
    public string? Version { get; }
}

/// <summary>
/// Reads and writes registered types as Redis keys.
/// </summary>
/// <remarks>
/// Not a unit of work. Redis has no transaction Wolverine could enlist a handler in, so every method
/// here takes effect immediately: a handler that writes two documents and then throws has written one
/// of them. The saga methods are individually atomic — each is one Lua script, and Redis runs a script
/// to completion before any other command — but two of them are still two separate writes.
/// </remarks>
public interface IRedisDocumentSession
{
    /// <summary>
    /// Load a document by its identity, or null when the key does not exist.
    /// </summary>
    Task<T?> LoadAsync<T>(object id, string? tenantId, CancellationToken token = default) where T : class;

    /// <summary>
    /// Write a document, overwriting whatever is at its key.
    /// </summary>
    Task StoreAsync<T>(T document, string? tenantId, CancellationToken token = default) where T : class;

    /// <summary>
    /// Delete the key a document lives at. Missing keys are not an error.
    /// </summary>
    Task DeleteAsync<T>(T document, string? tenantId, CancellationToken token = default) where T : class;

    Task DeleteByIdAsync<T>(object id, string? tenantId, CancellationToken token = default) where T : class;

    /// <summary>
    /// Read a saga together with the revision its next write must match.
    /// </summary>
    Task<RedisSagaState<T>> LoadSagaAsync<T>(object id, string? tenantId, CancellationToken token = default)
        where T : class;

    /// <summary>
    /// Create a saga, failing with <see cref="SagaConcurrencyException" /> if one already exists at that
    /// key — which is what a second node starting the same saga looks like.
    /// </summary>
    Task InsertSagaAsync<T>(T saga, string? tenantId, CancellationToken token = default) where T : class;

    /// <summary>
    /// Write a saga only if its stored revision is still <paramref name="version" />, otherwise throw
    /// <see cref="SagaConcurrencyException" />.
    /// </summary>
    Task UpdateSagaAsync<T>(T saga, string? version, string? tenantId, CancellationToken token = default)
        where T : class;

    /// <summary>
    /// Delete a completed saga only if its stored revision is still <paramref name="version" />,
    /// otherwise throw <see cref="SagaConcurrencyException" />.
    /// </summary>
    Task DeleteSagaAsync<T>(object id, string? version, string? tenantId, CancellationToken token = default)
        where T : class;
}
