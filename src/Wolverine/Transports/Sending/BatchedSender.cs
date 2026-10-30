using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;

namespace Wolverine.Transports.Sending;

public class BatchedSender : ISender, ISenderRequiresCallback, IConditionalNativeScheduling, IAsyncDisposable
{
    private readonly CancellationToken _cancellation;
    private readonly ILogger _logger;

    private readonly ISenderProtocol _protocol;
    private readonly int _batchSize;
    private readonly IBlock<Envelope> _serializing;
    private ISenderCallback? _callback;
    private int _queued;

    public BatchedSender(Endpoint destination, ISenderProtocol protocol, CancellationToken cancellation, ILogger logger)
    {
        Destination = destination.Uri;
        _protocol = protocol;
        _batchSize = Math.Max(1, destination.MessageBatchSize);
        _cancellation = cancellation;
        _logger = logger;

        var sender = new Block<OutgoingMessageBatch>(destination.MessageBatchMaxDegreeOfParallelism, SendBatchAsync);
        var transforming =
            sender.PushUpstream<Envelope[]>(envelopes => new OutgoingMessageBatch(Destination, envelopes));

        var batching = transforming.BatchUpstream(destination.MessageBatchTimeout, batchSize:destination.MessageBatchSize);

        // GH-3825: this stage MUST stay single threaded. It is the only thing between
        // SendAsync() and the outgoing batch, so any parallelism here hands envelopes to the
        // batching block in serialization-completion order rather than enqueue order -- which
        // silently destroys the FIFO guarantee that Azure Service Bus sessions, SQS FIFO groups,
        // and global partitioning all rest on. This was an ordered ActionBlock (MaxDegreeOfParallelism
        // defaults to 1) before the switch from TPL Dataflow to Channels; the rewrite raised it to
        // Environment.ProcessorCount and lost the guarantee along the way. Everything downstream is
        // already serial by default (Endpoint.MessageBatchMaxDegreeOfParallelism is 1).
        //
        // GetDataAsync lets IAsyncMessageSerializer implementations (schema registry, encryption)
        // serialize without blocking; plain serializers fall through to the synchronous Write. The check
        // is MessagePayloadSize rather than Data because reading Data serializes synchronously.
        _serializing = batching.PushUpstream<Envelope>(1, async (e, _) =>
        {
            try
            {
                if (e.MessagePayloadSize == null)
                {
                    await e.GetDataAsync().ConfigureAwait(false);
                }
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

    bool IConditionalNativeScheduling.CanScheduleNatively(Envelope envelope, DateTimeOffset utcNow)
    {
        return _protocol is not IConditionalNativeScheduling conditional ||
               conditional.CanScheduleNatively(envelope, utcNow);
    }

    public ValueTask SendAsync(Envelope message)
    {
        if (_serializing == null)
        {
            throw new InvalidOperationException("This agent has not been started");
        }

        // Paired with the per-batch decrement in SendBatchAsync's finally, so
        // QueuedCount reflects envelopes accepted but not yet flushed to the protocol
        Interlocked.Increment(ref _queued);
        return _serializing.PostAsync(message);
    }

    /// <summary>
    ///     GH-5034. Send one envelope straight through the transport protocol, bypassing the in-memory batching,
    ///     and only return once the broker has accepted it. Throws if it did not. Used where the caller deletes
    ///     the envelope's outbox row as soon as this returns, so nothing can be left to the background
    /// </summary>
    internal async Task SendThroughProtocolAsync(Envelope envelope)
    {
        if (Latched)
        {
            throw new InlineSendFailedException($"The sender to {Destination} is latched", null);
        }

        if (envelope.MessagePayloadSize == null)
        {
            await envelope.GetDataAsync().ConfigureAwait(false);
        }

        var callback = new InlineSendCallback();
        await _protocol.SendBatchAsync(callback, new OutgoingMessageBatch(Destination, [envelope]))
            .ConfigureAwait(false);

        callback.AssertAccepted(envelope);
    }

    /// <summary>
    ///     GH-5016. Send several envelopes straight through the transport protocol, bypassing the in-memory
    ///     batching, in batches of the endpoint's MessageBatchSize, one broker round trip per batch. Records in
    ///     <paramref name="result" /> which envelopes the broker accepted. A protocol that reports part of a batch
    ///     as failed, like SQS, has only that part left out
    /// </summary>
    internal async Task SendThroughProtocolAsync(IReadOnlyList<Envelope> envelopes, InlineSendResult result)
    {
        if (Latched)
        {
            var latched = new InlineSendFailedException($"The sender to {Destination} is latched", null);
            foreach (var envelope in envelopes) result.Fail(envelope, latched);
            return;
        }

        var ready = new List<Envelope>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            try
            {
                if (envelope.MessagePayloadSize == null)
                {
                    await envelope.GetDataAsync().ConfigureAwait(false);
                }

                ready.Add(envelope);
            }
            catch (Exception e)
            {
                result.Fail(envelope, e);
            }
        }

        foreach (var chunk in ready.Chunk(_batchSize))
        {
            var callback = new InlineSendCallback();
            try
            {
                await _protocol.SendBatchAsync(callback, new OutgoingMessageBatch(Destination, chunk))
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                foreach (var envelope in chunk) result.Fail(envelope, e);
                continue;
            }

            foreach (var envelope in chunk)
            {
                if (callback.WasAccepted(envelope))
                {
                    result.Accept(envelope);
                }
                else if (callback.IsSerializationFailure(envelope))
                {
                    result.FailSerialization(envelope, callback.FailureFor(envelope));
                }
                else
                {
                    result.Fail(envelope, callback.FailureFor(envelope));
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_protocol is IAsyncDisposable ad)
        {
            await ad.DisposeAsync().ConfigureAwait(false);
        }
        else if (_protocol is IDisposable d)
        {
            d.SafeDispose();
        }

        _serializing.Complete();
        await _serializing.DisposeAsync().ConfigureAwait(false);
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