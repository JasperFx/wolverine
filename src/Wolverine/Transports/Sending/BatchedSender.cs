using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Util.Dataflow;

namespace Wolverine.Transports.Sending;

public class BatchedSender : ISender, ISenderRequiresCallback
{
    private readonly BatchingBlock<Envelope> _batching;
    private readonly TransformBlock<Envelope[], OutgoingMessageBatch> _batchWriting;
    private readonly CancellationToken _cancellation;
    private readonly ILogger _logger;

    private readonly ISenderProtocol _protocol;
    private readonly ActionBlock<OutgoingMessageBatch> _sender;
    private readonly ActionBlock<Envelope> _serializing;
    private ISenderCallback? _callback;
    private int _queued;

    public BatchedSender(Uri destination, ISenderProtocol protocol, CancellationToken cancellation, ILogger logger)
    {
        Destination = destination;
        _protocol = protocol;
        _cancellation = cancellation;
        _logger = logger;

        _sender = new ActionBlock<OutgoingMessageBatch>(SendBatchAsync, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            CancellationToken = _cancellation,
            BoundedCapacity = DataflowBlockOptions.Unbounded
        });

        _serializing = new ActionBlock<Envelope>(async e =>
            {
                try
                {
                    await _batching!.SendAsync(e);
                }
                catch (Exception? ex)
                {
                    _logger.LogError(ex, "Error while trying to serialize envelope {Envelope}", e);
                }
            },
            new ExecutionDataflowBlockOptions
            {
                CancellationToken = _cancellation,
                BoundedCapacity = DataflowBlockOptions.Unbounded
            });

        _batchWriting = new TransformBlock<Envelope[], OutgoingMessageBatch>(
            envelopes =>
            {
                var batch = new OutgoingMessageBatch(Destination, envelopes);
                _queued += batch.Messages.Count;
                return batch;
            },
            new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = DataflowBlockOptions.Unbounded, MaxDegreeOfParallelism = 10,
                CancellationToken = _cancellation
            });

        _batchWriting.LinkTo(_sender);
        _batching = new BatchingBlock<Envelope>(200, _batchWriting, _cancellation);
    }

    public int QueuedCount => _queued + _batching.ItemCount + _serializing.InputCount;

    public bool Latched { get; private set; }

    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        if (_callback == null)
        {
            throw new InvalidOperationException("This sender has not been registered.");
        }

        var batch = OutgoingMessageBatch.ForPing(Destination);
        await _protocol.SendBatchAsync(_callback, batch);

        return true;
    }

    public bool SupportsNativeScheduledSend { get; } = false;

    public ValueTask SendAsync(Envelope message)
    {
        if (_batching == null)
        {
            throw new InvalidOperationException("This agent has not been started");
        }

        _serializing.Post(message);

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _serializing.Complete();
        _sender.Complete();
        _batching.Dispose();
    }

    public void RegisterCallback(ISenderCallback senderCallback)
    {
        _callback = senderCallback;
    }

    public Task LatchAndDrainAsync()
    {
        Latched = true;

        _sender.Complete();
        _serializing.Complete();
        _batchWriting.Complete();
        _batching.Complete();

        _logger.CircuitBroken(Destination);

        return Task.CompletedTask;
    }

    public async Task SendBatchAsync(OutgoingMessageBatch batch)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        if (_callback == null)
        {
            throw new InvalidOperationException("This sender has not been registered.");
        }

        try
        {
            if (Latched)
            {
                await _callback.MarkSenderIsLatchedAsync(batch);
            }
            else
            {
                await _protocol.SendBatchAsync(_callback, batch);

                _logger.OutgoingBatchSucceeded(batch);
            }
        }
        catch (Exception? e)
        {
            await batchSendFailedAsync(batch, e).ConfigureAwait(false);
        }

        finally
        {
            _queued -= batch.Messages.Count;
        }
    }

    private async Task batchSendFailedAsync(OutgoingMessageBatch batch, Exception? exception)
    {
        if (_callback == null)
        {
            throw new InvalidOperationException("This sender has not been registered.");
        }

        try
        {
            await _callback.MarkProcessingFailureAsync(batch, exception).ConfigureAwait(false);
        }
        catch (Exception? e)
        {
            _logger.LogError(e, "Error while trying to process a send failure");
        }
    }
}