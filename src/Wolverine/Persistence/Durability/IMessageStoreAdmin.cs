using Wolverine.Logging;

namespace Wolverine.Persistence.Durability;

public interface IMessageStoreAdmin
{
    Task DeleteAllHandledAsync();
    
    /// <summary>
    ///     Clears out all persisted envelopes
    /// </summary>
    /// <returns></returns>
    Task ClearAllAsync();

    /// <summary>
    ///     Rebuilds the envelope storage and clears out any
    ///     persisted state
    /// </summary>
    /// <returns></returns>
    Task RebuildAsync();

    /// <summary>
    ///     Access the current count of persisted envelopes
    /// </summary>
    /// <returns></returns>
    Task<PersistedCounts> FetchCountsAsync();

    /// <summary>
    ///     Fetch all persisted incoming envelopes
    /// </summary>
    /// <returns></returns>
    Task<IReadOnlyList<Envelope>> AllIncomingAsync();

    /// <summary>
    ///     Fetch all persisted, outgoing envelopes
    /// </summary>
    /// <returns></returns>
    Task<IReadOnlyList<Envelope>> AllOutgoingAsync();

    /// <summary>
    ///     Release the node specific ownership of all persisted envelopes so they
    ///     may be claimed by other nodes
    /// </summary>
    /// <returns></returns>
    Task ReleaseAllOwnershipAsync();
    
    /// <summary>
    ///     Release the node specific ownership of all persisted envelopes for a specific node so they
    ///     may be claimed by other nodes
    /// </summary>
    /// <returns></returns>
    Task ReleaseAllOwnershipAsync(int ownerId);

    /// <summary>
    ///     Merely checks whether or not the system can connect to the durable envelope storage as an
    ///     environment check
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task CheckConnectivityAsync(CancellationToken token);

    /// <summary>
    ///     Verify that the durable envelope storage objects (schema/tables) actually exist and match the
    ///     configured schema, throwing when they do not. Used by <c>resources check</c> so a missing or
    ///     un-provisioned schema is reported as unhealthy rather than passing on connectivity alone.
    ///     The default is a no-op for stores that don't support schema introspection.
    /// </summary>
    Task AssertStorageExistsAsync(CancellationToken token) => Task.CompletedTask;

    /// <summary>
    ///     Apply any necessary database migrations to bring the underlying envelope
    ///     storage to the configured requirements of the Wolverine system
    /// </summary>
    /// <returns></returns>
    Task MigrateAsync();
}