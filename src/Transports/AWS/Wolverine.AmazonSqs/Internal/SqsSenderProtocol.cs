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

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _queue.InitializeAsync(_logger);

        // SQS has a hard limit of 10 messages per batch request
        var chunks = batch.Messages.Chunk(10).ToArray();

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
