using Wolverine.Logging;

namespace Wolverine.Persistence.Durability;

public interface IMessageStoreAdmin
{
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
    ///     Apply any necessary database migrations to bring the underlying envelope
    ///     storage to the configured requirements of the Wolverine system
    /// </summary>
    /// <returns></returns>
    Task MigrateAsync();
}