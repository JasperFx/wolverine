using JasperFx.Blocks;
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

    public BatchingProcessor(HandlerChain chain, IMessageBatcher batcher, BatchingOptions options, ILocalQueue queue,
        DurabilitySettings settings, BatchingPendingCounts? pendingCounts = null)
        : this(chain, batcher, options, queue, new SingleBatchExecutionQueue(queue), settings, pendingCounts)
    {
    }

    internal BatchingProcessor(HandlerChain chain, IMessageBatcher batcher, BatchingOptions options, ILocalQueue queue,
        IBatchExecutionQueues queues, DurabilitySettings settings, BatchingPendingCounts? pendingCounts = null)
    {
        _pendingCounts = pendingCounts;
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
        foreach (var grouped in Batcher.Group(envelopes))
        {
            // GH-3867 — the destination is per batch, not per processor: a batch that carries a group
            // id lands on that group's partition slot, so it is sequenced against the unbatched
            // handlers for the same group rather than racing them from a queue of its own.
            var queue = _queues.SelectQueue(grouped);

            grouped.Destination = queue.Uri;
            grouped.MessageType = Chain!.TypeName;
            grouped.SentAt = DateTimeOffset.UtcNow;

            await queue.EnqueueAsync(grouped);
        }
    }
}