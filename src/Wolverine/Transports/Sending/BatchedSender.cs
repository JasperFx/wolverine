using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;

namespace Wolverine.Transports.Sending;

public class BatchedSender : ISender, ISenderRequiresCallback
{
    private readonly CancellationToken _cancellation;
    private readonly ILogger _logger;

    private readonly ISenderProtocol _protocol;
    private readonly IBlock<Envelope> _serializing;
    private ISenderCallback? _callback;
    private int _queued;

    public BatchedSender(Endpoint destination, ISenderProtocol protocol, CancellationToken cancellation, ILogger logger)
    {
        Destination = destination.Uri;
        _protocol = protocol;
        _cancellation = cancellation;
        _logger = logger;

        var sender = new Block<OutgoingMessageBatch>(destination.MessageBatchMaxDegreeOfParallelism, SendBatchAsync);
        var transforming =
            sender.PushUpstream<Envelope[]>(envelopes => new OutgoingMessageBatch(Destination, envelopes));

        var batching = transforming.BatchUpstream(250.Milliseconds());
        _serializing = batching.PushUpstream<Envelope>(Environment.ProcessorCount, e =>
        {
            try
            {
                if (e.Data == null && e.Serializer != null)
                {
                    e.Data = e.Serializer.Write(e);
                }

                return e;
            }
            catch (Exception? ex)
            {
                _logger.LogError(ex, "Error while trying to serialize envelope {Envelope}", e);
            }

            return e;
        });

        SupportsNativeScheduledSend = _protocol is ISenderProtocolWithNativeScheduling;
    }

    public int QueuedCount => _queued;

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

    public bool SupportsNativeScheduledSend { get; set; }

    public ValueTask SendAsync(Envelope message)
    {
        if (_serializing == null)
        {
            throw new InvalidOperationException("This agent has not been started");
        }

        return _serializing.PostAsync(message);
    }

    public void Dispose()
    {
        if (_protocol is IDisposable d)
        {
            d.SafeDispose();
        }

        _serializing.Complete();
        _serializing.SafeDisposeSynchronously();
    }

    public void RegisterCallback(ISenderCallback senderCallback)
    {
        _callback = senderCallback;
    }

    public async Task LatchAndDrainAsync()
    {
        Latched = true;

        await _serializing.WaitForCompletionAsync();

        _logger.CircuitBroken(Destination);
    }

    public async Task SendBatchAsync(OutgoingMessageBatch batch, CancellationToken _)
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
            Interlocked.Add(ref _queued, -batch.Messages.Count);
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