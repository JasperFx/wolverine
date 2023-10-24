using System.Threading.Tasks.Dataflow;
using JasperFx.Core;

namespace Wolverine.Util.Dataflow;

public class BatchingBlock<T> : IDisposable
{
    private readonly BatchBlock<T> _batchBlock;
    private readonly TimeSpan _timeSpan;
    private readonly Timer _trigger;
    
    public BatchingBlock(int milliseconds, ITargetBlock<T[]> processor,
        CancellationToken cancellation = default)
        : this(milliseconds.Milliseconds(), processor, 100, cancellation)
    {
    }
    

    public BatchingBlock(TimeSpan timeSpan, ITargetBlock<T[]> processor, int batchSize = 100,
        CancellationToken cancellation = default)
    {
        _timeSpan = timeSpan;
        _batchBlock = new BatchBlock<T>(batchSize, new GroupingDataflowBlockOptions
        {
            CancellationToken = cancellation,
            BoundedCapacity = DataflowBlockOptions.Unbounded
        });

        _trigger = new Timer(_ =>
        {
            try
            {
                _batchBlock.TriggerBatch();
            }
            catch (Exception)
            {
                // ignored
            }
        }, null, Timeout.Infinite, Timeout.Infinite);


        _batchBlock.LinkTo(processor);
    }

    public int ItemCount => _batchBlock.OutputCount;

    public Task Completion => _batchBlock.Completion;


    public void Dispose()
    {
        _trigger.Dispose();
        _batchBlock.Complete();
    }

    public void Send(T item)
    {
        try
        {
            _trigger.Change(_timeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (Exception)
        {
            // ignored
        }

        _batchBlock.Post(item);
    }

    public Task SendAsync(T item)
    {
        try
        {
            _trigger.Change(_timeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (Exception)
        {
            // ignored
        }

        return _batchBlock.SendAsync(item);
    }

    public void Complete()
    {
        _batchBlock.Complete();
    }
}