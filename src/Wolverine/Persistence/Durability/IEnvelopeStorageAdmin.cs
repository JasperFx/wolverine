using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Logging;

namespace Wolverine.Persistence.Durability;

public interface IEnvelopeStorageAdmin
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

    Task<IReadOnlyList<Envelope>> AllIncomingAsync();
    Task<IReadOnlyList<Envelope>> AllOutgoingAsync();


    Task ReleaseAllOwnershipAsync();

    public Task CheckAsync(CancellationToken token);
    
    /// <summary>
    /// Apply any necessary database migrations to bring the underlying envelope
    /// storage to the configured requirements of the Wolverine system
    /// </summary>
    /// <returns></returns>
    Task MigrateAsync();
}
