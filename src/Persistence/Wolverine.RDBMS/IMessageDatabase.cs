using System.Data.Common;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS;

public interface IMessageDatabase : IMessageStore
{
    public DurabilitySettings Durability { get; }

    public DatabaseSettings Settings { get; }
    IDurableStorageSession Session { get; }

    Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes);
    Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes);

    Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing);
    Task<Uri[]> FindAllDestinationsAsync();


    Task<IReadOnlyList<Envelope>> LoadScheduledToExecuteAsync(DateTimeOffset utcNow);

    Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming);

    /// <summary>
    ///     Access the current count of persisted envelopes
    /// </summary>
    /// <returns></returns>
    Task<PersistedCounts> FetchCountsAsync();

    Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit);
}