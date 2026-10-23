using ImTools;
using Microsoft.Extensions.Logging;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5171. One <see cref="EnvelopeStoreCoalescer" /> per database behind a store that fans out to
///     several of them -- a multi-tenanted store, or a main store with ancillary stores. A plain
///     coalescer runs one flush at a time, so with a database per tenant one tenant's slow database held
///     up every other tenant's outbox writes and deletes on the same endpoint, and the callers awaiting
///     them on the request path. Here each database gets its own lane, flushes on its own, and a slow
///     one only delays its own envelopes.
/// </summary>
/// <remarks>
///     Only used when the store can reach more than one database. A single-database store keeps the
///     plain <see cref="EnvelopeStoreCoalescer" /> with no extra lookup at all.
/// </remarks>
internal sealed class PartitionedEnvelopeStoreCoalescer : IEnvelopeStoreCoalescer
{
    private readonly Func<Envelope, object> _partitionFor;
    private readonly Func<IReadOnlyList<Envelope>, Task> _storeBatch;
    private readonly Func<Envelope, Task> _storeOne;
    private readonly int _maximumBatchSize;
    private readonly Uri _uri;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private ImHashMap<object, EnvelopeStoreCoalescer> _lanes = ImHashMap<object, EnvelopeStoreCoalescer>.Empty;

    /// <param name="partitionFor">
    ///     The key of the database an envelope's row lives in. Envelopes with equal keys share a lane,
    ///     and must, because they share a batched write
    /// </param>
    public PartitionedEnvelopeStoreCoalescer(Func<Envelope, object> partitionFor,
        Func<IReadOnlyList<Envelope>, Task> storeBatch, Func<Envelope, Task> storeOne, int maximumBatchSize, Uri uri,
        ILogger logger)
    {
        _partitionFor = partitionFor;
        _storeBatch = storeBatch;
        _storeOne = storeOne;
        _maximumBatchSize = maximumBatchSize;
        _uri = uri;
        _logger = logger;
    }

    /// <summary>
    ///     Number of lanes created so far. Exposed for tests.
    /// </summary>
    public int LaneCount => _lanes.Enumerate().Count();

    public Task StoreAsync(Envelope envelope)
    {
        var key = _partitionFor(envelope);
        if (!_lanes.TryFind(key, out var lane))
        {
            lane = addLane(key);
        }

        return lane.StoreAsync(envelope);
    }

    public Task DrainAsync()
    {
        var lanes = _lanes;
        if (lanes.IsEmpty)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(lanes.Enumerate().Select(x => x.Value.DrainAsync()));
    }

    private EnvelopeStoreCoalescer addLane(object key)
    {
        lock (_lock)
        {
            if (_lanes.TryFind(key, out var lane))
            {
                return lane;
            }

            lane = new EnvelopeStoreCoalescer(_storeBatch, _storeOne, _maximumBatchSize, _uri, _logger);
            _lanes = _lanes.AddOrUpdate(key, lane);
            return lane;
        }
    }
}

/// <summary>
///     GH-5171. Picks the coalescer for a store: one lane per database when the store can fan out to
///     several databases, the plain single-lane coalescer otherwise.
/// </summary>
internal static class EnvelopeStoreCoalescing
{
    public static IEnvelopeStoreCoalescer Build(Func<Envelope, object>? partitionFor,
        Func<IReadOnlyList<Envelope>, Task> storeBatch, Func<Envelope, Task> storeOne, int maximumBatchSize, Uri uri,
        ILogger logger)
    {
        return partitionFor == null
            ? new EnvelopeStoreCoalescer(storeBatch, storeOne, maximumBatchSize, uri, logger)
            : new PartitionedEnvelopeStoreCoalescer(partitionFor, storeBatch, storeOne, maximumBatchSize, uri, logger);
    }

    /// <summary>
    ///     How envelopes written through this outbox map to databases, or null when there is only one
    /// </summary>
    public static Func<Envelope, object>? PartitionerFor(IMessageOutbox outbox)
    {
        return outbox switch
        {
            MultiTenantedMessageStore tenanted => e => tenanted.CoalescingPartitionFor(e.TenantId),
            DelegatingMessageOutbox delegating => delegating.CoalescingPartitionFor,
            _ => null
        };
    }

    /// <summary>
    ///     How envelopes written through this inbox map to databases, or null when there is only one
    /// </summary>
    public static Func<Envelope, object>? PartitionerFor(IMessageInbox inbox)
    {
        return inbox switch
        {
            MultiTenantedMessageStore tenanted => e => tenanted.CoalescingPartitionFor(e.TenantId),
            DelegatingMessageInbox delegating => delegating.CoalescingPartitionFor,
            _ => null
        };
    }

    /// <summary>
    ///     The partition of an envelope that a delegating inbox or outbox routes by
    ///     <see cref="Envelope.Store" />, falling back to <paramref name="inner" /> when it has none
    /// </summary>
    internal static object PartitionFor(Envelope envelope, object inner)
    {
        return envelope.Store switch
        {
            MultiTenantedMessageStore tenanted => tenanted.CoalescingPartitionFor(envelope.TenantId),
            { } store => store,
            null => inner is MultiTenantedMessageStore innerTenanted
                ? innerTenanted.CoalescingPartitionFor(envelope.TenantId)
                : inner
        };
    }
}
