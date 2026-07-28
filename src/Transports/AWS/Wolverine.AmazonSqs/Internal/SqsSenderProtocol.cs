using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsSenderProtocol : ISenderProtocolWithNativeScheduling, IConditionalNativeScheduling
{
    private readonly ILogger _logger;
    private readonly AmazonSqsQueue _queue;
    private readonly IAmazonSQS _sqs;

    public SqsSenderProtocol(IWolverineRuntime runtime, AmazonSqsQueue queue, IAmazonSQS sqs)
    {
        _queue = queue;
        _sqs = sqs;
        _logger = runtime.LoggerFactory.CreateLogger<SqsSenderProtocol>();

        _queue.Mapper ??= _queue.BuildMapper(runtime);
    }

    // Standard queues can delay individual messages natively (DelaySeconds, max 15 minutes);
    // FIFO queues only support a queue-level delay, so they never schedule natively
    bool IConditionalNativeScheduling.CanScheduleNatively(Envelope envelope, DateTimeOffset utcNow)
    {
        return _queue.CanScheduleNatively(envelope, utcNow);
    }

    /// <summary>
    ///     Amazon SQS caps an entire <c>SendMessageBatch</c> request at 256KB, not just the
    ///     individual messages. Chunk below that with room to spare, since the entry size here is
    ///     an estimate (GH-3493).
    /// </summary>
    internal const int MaximumBatchPayloadBytes = 240 * 1024;

    /// <summary>
    ///     Fixed allowance per entry for the serialized envelope's headers and the SQS entry
    ///     scaffolding, added before the base64 inflation. Over-estimating costs an extra request;
    ///     under-estimating bounces a whole batch.
    /// </summary>
    internal const int EntryOverheadBytes = 1024;

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _queue.InitializeAsync(_logger);

        var chunks = ChunkMessages(batch.Messages);

        var successes = new List<Envelope>();
        var failures = new List<Envelope>();

        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk = chunks[i];
            var sqsBatch = new OutgoingSqsBatch(_queue, _logger, chunk);

            try
            {
                var response = await _sqs.SendMessageBatchAsync(sqsBatch.Request);
                sortChunkResults(sqsBatch, chunk, response, successes, failures);
            }
            catch (Exception e)
            {
                // This chunk (and any chunk after it) never made it to SQS, but earlier
                // chunks may already have been accepted, so only fail what actually failed
                var unsent = chunks.Skip(i).SelectMany(x => x);

                if (successes.Count != 0)
                {
                    await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, successes));
                }

                await callback.MarkProcessingFailureAsync(
                    new OutgoingMessageBatch(batch.Destination, failures.Concat(unsent).ToList()), e);

                return;
            }
        }

        if (failures.Count == 0)
        {
            await callback.MarkSuccessfulAsync(batch);
        }
        else
        {
            if (successes.Count != 0)
            {
                await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, successes));
            }

            await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, failures));
        }
    }

    /// <summary>
    ///     Split an outgoing batch on BOTH of SQS's limits: at most 10 entries per request, and at
    ///     most 256KB across the whole request. Chunking on the count alone let ten individually
    ///     legal 30KB messages bounce the entire request (GH-3493). A single message that is over
    ///     the limit on its own still gets its own request and fails there, exactly as before.
    /// </summary>
    internal static Envelope[][] ChunkMessages(IEnumerable<Envelope> envelopes)
    {
        var chunks = new List<Envelope[]>();
        var current = new List<Envelope>(10);
        var currentSize = 0;

        foreach (var envelope in envelopes)
        {
            var size = EstimateEntrySize(envelope);

            if (current.Count > 0 && (current.Count == 10 || currentSize + size > MaximumBatchPayloadBytes))
            {
                chunks.Add(current.ToArray());
                current.Clear();
                currentSize = 0;
            }

            current.Add(envelope);
            currentSize += size;
        }

        if (current.Count > 0)
        {
            chunks.Add(current.ToArray());
        }

        return chunks.ToArray();
    }

    /// <summary>
    ///     Estimated wire size of one batch entry. The default mapper serializes the whole envelope
    ///     -- headers included -- and base64 encodes it, which inflates by 4/3.
    /// </summary>
    internal static int EstimateEntrySize(Envelope envelope)
    {
        var raw = (envelope.Data?.Length ?? 0) + EntryOverheadBytes;
        return (raw + 2) / 3 * 4;
    }

    // SendMessageBatchAsync is not transactional -- SQS can accept some entries and reject
    // others (throttling, oversized message, etc.) in the very same 200 response, so every
    // entry in response.Failed has to be routed back through the sender callback for retry
    private void sortChunkResults(OutgoingSqsBatch sqsBatch, Envelope[] chunk, SendMessageBatchResponse response,
        List<Envelope> successes, List<Envelope> failures)
    {
        if (response.Failed == null || response.Failed.Count == 0)
        {
            successes.AddRange(chunk);
            return;
        }

        var failed = new HashSet<Envelope>();
        foreach (var entry in response.Failed)
        {
            if (sqsBatch.TryGetEnvelope(entry.Id, out var envelope))
            {
                _logger.LogError(
                    "SQS batch send to {Uri} failed for message {Id}: {Code} - {Message} (SenderFault: {SenderFault}). The message will be retried",
                    _queue.Uri, entry.Id, entry.Code, entry.Message, entry.SenderFault);
                failed.Add(envelope);
            }
            else
            {
                _logger.LogError(
                    "SQS batch send to {Uri} reported a failed entry with unrecognized Id {Id}: {Code} - {Message}",
                    _queue.Uri, entry.Id, entry.Code, entry.Message);
            }
        }

        foreach (var envelope in chunk)
        {
            if (failed.Contains(envelope))
            {
                failures.Add(envelope);
            }
            else
            {
                successes.Add(envelope);
            }
        }
    }
}

internal class OutgoingSqsBatch
{
    private readonly Dictionary<string, Envelope> _envelopes = new();

    public OutgoingSqsBatch(AmazonSqsQueue queue, ILogger logger, IEnumerable<Envelope> envelopes)
    {
        var entries = new List<SendMessageBatchRequestEntry>();
        foreach (var envelope in envelopes)
        {
            try
            {
                var entry = new SendMessageBatchRequestEntry(envelope.Id.ToString(), queue.Mapper!.BuildMessageBody(envelope));
                if (queue.IsFifoQueue)
                {
                    var groupId = queue.Mapper.DetermineGroupId(envelope);
                    if (groupId.IsNotEmpty())
                    {
                        entry.MessageGroupId = groupId;
                    }
                    if (envelope.DeduplicationId.IsNotEmpty())
                    {
                        entry.MessageDeduplicationId = envelope.DeduplicationId;
                    }
                }
                else if (queue.EnableFairQueueMessageGroups)
                {
                    // SQS fair queues: a MessageGroupId on a standard queue improves tenant fairness.
                    // No deduplication semantics apply to standard queues. See GH-2886.
                    var groupId = queue.Mapper.DetermineGroupId(envelope);
                    if (groupId.IsNotEmpty())
                    {
                        entry.MessageGroupId = groupId;
                    }
                }

                foreach (var attribute in queue.Mapper.ToAttributes(envelope))
                {
                    entry.MessageAttributes ??= new Dictionary<string, MessageAttributeValue>();
                    entry.MessageAttributes.Add(attribute.Key, attribute.Value);
                }

                var delaySeconds = queue.NativeDelaySecondsFor(envelope, DateTimeOffset.UtcNow, logger);
                if (delaySeconds > 0)
                {
                    entry.DelaySeconds = delaySeconds;
                }

                entries.Add(entry);
                _envelopes.Add(entry.Id, envelope);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while mapping envelope {Envelope} to an SQS SendMessageBatchRequestEntry",
                    envelope);
            }
        }

        Request = new SendMessageBatchRequest(queue.QueueUrl, entries);
    }

    public SendMessageBatchRequest Request { get; }

    public bool TryGetEnvelope(string id, out Envelope envelope)
    {
        return _envelopes.TryGetValue(id, out envelope!);
    }
}
