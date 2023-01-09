using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    ///     Marks the Envelopes in DeadLetterTable
    ///     as replayable. DurabilityAgent will move the envelopes to IncomingTable. 
    /// </summary>
    /// <param name="exceptionType">Exception Type that should be marked. Default is any.</param>
    /// <returns>Number of envelopes marked.</returns>
    Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType = "");

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