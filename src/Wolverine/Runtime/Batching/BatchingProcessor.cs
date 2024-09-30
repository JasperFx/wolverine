using System.Diagnostics;
using System.Threading.Tasks.Dataflow;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Util.Dataflow;

namespace Wolverine.Runtime.Batching;

public class BatchingProcessor<T> : MessageHandler, IAsyncDisposable
{
    private readonly BatchingOptions _options;
    private readonly ActionBlock<Envelope[]> _processingBlock;
    private readonly BatchingBlock<Envelope> _batchingBlock;

    public BatchingProcessor(HandlerChain chain, IMessageBatcher batcher, BatchingOptions options, ILocalQueue queue,
        DurabilitySettings settings)
    {
        Chain = chain ?? throw new ArgumentOutOfRangeException(nameof(chain));
        _options = options;
        Batcher = batcher;
        Queue = queue;

        _processingBlock = new ActionBlock<Envelope[]>(processEnvelopes);
        _batchingBlock = new BatchingBlock<Envelope>(_options.TriggerTime, _processingBlock, _options.BatchSize, settings.Cancellation);
    }


    public IMessageBatcher Batcher { get; }
    public ILocalQueue Queue { get; }

    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        context.Envelope!.InBatch = true;
        return _batchingBlock.SendAsync(context.Envelope);
    }
    
    private Task processEnvelopes(Envelope[] envelopes)
    {
        foreach (var grouped in Batcher.Group(envelopes).ToArray())
        {
            grouped.Destination = Queue.Uri;
            grouped.MessageType = Chain!.TypeName;
            grouped.SentAt = DateTimeOffset.UtcNow;
            
            Queue.Enqueue(grouped);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _batchingBlock.Complete();
        _batchingBlock.Dispose();

        return new ValueTask();
    }
}