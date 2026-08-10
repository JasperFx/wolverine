using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Runtime.Batching;

public class BatchingProcessor<T> : MessageHandler, IAsyncDisposable
{
    private readonly BatchingChannel<Envelope> _batchingBlock;
    private readonly BatchingOptions _options;
    private readonly Block<Envelope[]> _processingBlock;

    private readonly BatchingPendingCounts? _pendingCounts;

    private readonly IBatchExecutionQueues _queues;

    private readonly ILogger? _logger;

    public BatchingProcessor(HandlerChain chain, IMessageBatcher batcher, BatchingOptions options, ILocalQueue queue,
        DurabilitySettings settings, BatchingPendingCounts? pendingCounts = null, ILogger? logger = null)
        : this(chain, batcher, options, queue, new SingleBatchExecutionQueue(queue), settings, pendingCounts, logger)
    {
    }

    internal BatchingProcessor(HandlerChain chain, IMessageBatcher batcher, BatchingOptions options, ILocalQueue queue,
        IBatchExecutionQueues queues, DurabilitySettings settings, BatchingPendingCounts? pendingCounts = null,
        ILogger? logger = null)
    {
        _pendingCounts = pendingCounts;
        _logger = logger;
        Chain = chain ?? throw new ArgumentOutOfRangeException(nameof(chain));

        _options = options;
        Batcher = batcher ?? throw new ArgumentNullException(nameof(batcher));
        Queue = queue;
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));

        _processingBlock = new Block<Envelope[]>(processEnvelopes);
        _batchingBlock = new BatchingChannel<Envelope>(_options.TriggerTime, _processingBlock, _options.BatchSize);
    }


    public IMessageBatcher Batcher { get; }

    /// <summary>
    /// The batch's dedicated local queue. Note that an individual batch does not necessarily execute
    /// here — when the element type belongs to a partitioned topology, each batch runs on the slot
    /// for its own group id. See GH-3867.
    /// </summary>
    public ILocalQueue Queue { get; }

    public async ValueTask DisposeAsync()
    {
        _batchingBlock.Complete();
        await _batchingBlock.DisposeAsync();
    }

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope!;
        envelope.InBatch = true;

        // CritterWatch#942 — count this member as pending against its originating listener so the
        // listener's QueueCount (and therefore back-pressure) sees the batching pipeline's depth.
        // Envelopes with no listener are local sends/cascades and are deliberately not counted —
        // back-pressure can only ever pause an external listener. Settlement happens once per
        // grouped batch envelope at its terminal (BatchingPendingCounts.SettleBatch).
        _pendingCounts?.Increment(envelope.Listener?.Address);

        try
        {
            await _batchingBlock.PostAsync(envelope).ConfigureAwait(false);
        }
        catch
        {
            _pendingCounts?.Decrement(envelope.Listener?.Address);
            throw;
        }
    }

    private async Task processEnvelopes(Envelope[] envelopes, CancellationToken _)
    {
        // GH-3898 — member-level DeliverBy enforcement. An unbatched envelope's expiry is checked at
        // execution time, but a batched member executes as part of a grouped envelope, so any member
        // whose DeliverBy elapsed while it waited in the batching channel has to be shed here, at
        // batch assembly — after this point its payload is baked into the batch message and cannot be
        // removed generically. Shed members still need their terminal bookkeeping (inbox mark-handled
        // and back-pressure settlement), so they ride an already-expired carrier envelope through the
        // pipeline: its execution-time expiry check discards the carrier before any handler runs, and
        // the discard's CompleteAsync is a normal batch terminal.
        var (live, expired) = PartitionByExpiration(envelopes);

        if (expired.Length > 0)
        {
            // Same observability an unbatched expiry discard gets (event id 208), once per member
            _logger?.DiscardedExpired(expired);

            var carrier = BuildExpiredMemberCarrier(expired);
            var carrierQueue = _queues.SelectQueue(carrier);
            carrier.Destination = carrierQueue.Uri;
            carrier.MessageType = Chain!.TypeName;
            carrier.SentAt = DateTimeOffset.UtcNow;

            await carrierQueue.EnqueueAsync(carrier);
        }

        foreach (var grouped in Batcher.Group(live))
        {
            // GH-3867 — the destination is per batch, not per processor: a batch that carries a group
            // id lands on that group's partition slot, so it is sequenced against the unbatched
            // handlers for the same group rather than racing them from a queue of its own.
            var queue = _queues.SelectQueue(grouped);

            grouped.Destination = queue.Uri;
            grouped.MessageType = Chain!.TypeName;
            grouped.SentAt = DateTimeOffset.UtcNow;

            // GH-3898 — whole-batch backstop for expiry that elapses while the assembled batch waits
            // on the (deliberately unbounded, GH-3287) execution queue. Only the LATEST member expiry
            // is safe as a batch-level DeliverBy: by the time it has passed, every member is expired,
            // so discarding the whole batch never over-sheds. Any member without a DeliverBy keeps
            // the batch alive (null). The discard path settles all members exactly like a success.
            grouped.DeliverBy = LatestMemberExpiry(grouped.Batch);

            await queue.EnqueueAsync(grouped);
        }
    }

    /// <summary>
    /// GH-3898 — split a flush of the batching channel into still-deliverable members and members
    /// whose DeliverBy elapsed while they waited to be batched. Single pass, so a member cannot
    /// land in both (or neither) bucket when the clock ticks mid-partition.
    /// </summary>
    internal static (Envelope[] Live, Envelope[] Expired) PartitionByExpiration(Envelope[] envelopes)
    {
        List<Envelope>? expired = null;
        var live = new List<Envelope>(envelopes.Length);

        foreach (var envelope in envelopes)
        {
            if (envelope.IsExpired())
            {
                (expired ??= []).Add(envelope);
            }
            else
            {
                live.Add(envelope);
            }
        }

        return expired == null
            ? (envelopes, [])
            : (live.ToArray(), expired.ToArray());
    }

    /// <summary>
    /// GH-3898 — a degenerate batch envelope that exists only to carry expired members to a batch
    /// terminal. Its DeliverBy is the latest member expiry, which is already past, so the handler
    /// pipeline's execution-time check discards it before any handler could run — and the discard's
    /// CompleteAsync settles back-pressure counts and marks every member handled through the same
    /// machinery a successful batch uses.
    /// </summary>
    internal static Envelope BuildExpiredMemberCarrier(Envelope[] expired)
    {
        var latest = default(DateTimeOffset);
        foreach (var member in expired)
        {
            // IsExpired() == true implies DeliverBy.HasValue
            if (member.DeliverBy!.Value > latest)
            {
                latest = member.DeliverBy.Value;
            }
        }

        return new Envelope(Array.Empty<T>(), expired)
        {
            DeliverBy = latest
        };
    }

    /// <summary>
    /// GH-3898 — the batch-level DeliverBy backstop: the latest member expiry when every member
    /// carries one, else null (a member with no DeliverBy never expires, so neither may its batch).
    /// </summary>
    internal static DateTimeOffset? LatestMemberExpiry(Envelope[]? members)
    {
        if (members is not { Length: > 0 })
        {
            return null;
        }

        var latest = default(DateTimeOffset);
        foreach (var member in members)
        {
            if (member.DeliverBy is not { } deliverBy)
            {
                return null;
            }

            if (deliverBy > latest)
            {
                latest = deliverBy;
            }
        }

        return latest;
    }
}