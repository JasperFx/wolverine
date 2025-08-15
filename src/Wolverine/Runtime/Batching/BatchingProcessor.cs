using System.Diagnostics;
using System.Threading.Tasks.Dataflow;
using JasperFx.Blocks;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Runtime.Batching;

public class BatchingProcessor<T> : MessageHandler, IAsyncDisposable
{
    private readonly BatchingOptions _options;
    private readonly Block<Envelope[]> _processingBlock;
    private readonly BatchingChannel<Envelope> _batchingBlock;

    public BatchingProcessor(HandlerChain chain, IMessageBatcher batcher, BatchingOptions options, ILocalQueue queue,
        DurabilitySettings settings)
    {
        Chain = chain ?? throw new ArgumentOutOfRangeException(nameof(chain));
        
        _options = options;
        Batcher = batcher ?? throw new ArgumentNullException(nameof(batcher));
        Queue = queue;

        _processingBlock = new Block<Envelope[]>(processEnvelopes);
        _batchingBlock = new BatchingChannel<Envelope>(_options.TriggerTime, _processingBlock, _options.BatchSize);
    }


    public IMessageBatcher Batcher { get; }
    public ILocalQueue Queue { get; }

    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        context.Envelope!.InBatch = true;
        return _batchingBlock.PostAsync(context.Envelope).AsTask();
    }
    
    private Task processEnvelopes(Envelope[] envelopes, CancellationToken _)
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

    public async ValueTask DisposeAsync()
    {
        _batchingBlock.Complete();
        await _batchingBlock.DisposeAsync();
    }
}