using System.Diagnostics;
using System.Threading.Tasks.Dataflow;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Util.Dataflow;

namespace Wolverine.Runtime.Batching;

public class BatchingProcessor<T> : MessageHandler, IAsyncDisposable
{
    private readonly BatchingOptions _options;
    private readonly ActionBlock<Envelope[]> _processingBlock;
    private readonly BatchingBlock<Envelope> _batchingBlock;

    public BatchingProcessor(HandlerChain chain, BatchingOptions options, ILocalQueue queue,
        DurabilitySettings settings)
    {
        Chain = chain;
        _options = options;
        Queue = queue;

        _processingBlock = new ActionBlock<Envelope[]>(processEnvelopes);
        _batchingBlock = new BatchingBlock<Envelope>(_options.TriggerTime, _processingBlock, _options.BatchSize, settings.Cancellation);
    }


    public ILocalQueue Queue { get; }

    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        context.Envelope.InBatch = true;
        return _batchingBlock.SendAsync(context.Envelope);
    }
    
    private Task processEnvelopes(Envelope[] envelopes)
    {
        // Group by tenant id
        var groups = envelopes.GroupBy(x => x.TenantId).ToArray();
        foreach (var group in groups)
        {
            var message = group.Select(x => x.Message).OfType<T>().ToArray();
            
Debug.WriteLine($"SENDING {group.Count()} messages to tenant '{group.Key}'");            
            
            foreach (var envelope in group)
            {
                envelope.InBatch = true;
            }
            
            var grouped = new Envelope(message)
            {
                Destination = Queue.Uri,
                Batch = envelopes,
                MessageType = Chain.TypeName,
                SentAt = DateTimeOffset.UtcNow,
                TenantId = group.Key
            };

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