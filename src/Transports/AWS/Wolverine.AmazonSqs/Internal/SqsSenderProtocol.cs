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

        // GH-3926: turn each envelope into the SQS entries it actually needs - one normally, or N when
        // the endpoint opted into fragmenting and the body is over SQS's limit. Done BEFORE chunking so
        // that the 10-entry and 256KB-per-request limits are applied to what is really being sent.
        var units = new List<SqsSendUnit>(batch.Messages.Count);
        var unsendable = new List<Envelope>();

        foreach (var envelope in batch.Messages)
        {
            if (TryBuildUnits(envelope, out var built))
            {
                units.AddRange(built);
            }
            else
            {
                unsendable.Add(envelope);
            }
        }

        if (unsendable.Count != 0)
        {
            // Permanently unsendable, not a transient failure: SQS answers an oversized message with a
            // SenderFault, and retrying it produces the same SenderFault forever. That infinite retry is
            // how this presents in production - a flood of identical errors rather than one. Route it to
            // the serialization-failure path, which logs and drops rather than re-queueing.
            await callback.MarkSerializationFailureAsync(
                new OutgoingMessageBatch(batch.Destination, unsendable));
        }

        if (units.Count == 0) return;

        var chunks = ChunkUnits(units);

        // An envelope is only successful when EVERY unit it produced was accepted, and its fragments can
        // land in different requests, so failure is tracked by envelope across the whole send rather than
        // accumulated per chunk. Half a message in SQS is not a delivery.
        var failed = new HashSet<Envelope>();

        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk = chunks[i];
            var sqsBatch = new OutgoingSqsBatch(_queue, _logger, chunk);

            try
            {
                var response = await _sqs.SendMessageBatchAsync(sqsBatch.Request);
                sortChunkResults(sqsBatch, response, failed);
            }
            catch (Exception e)
            {
                // This chunk (and any chunk after it) never made it to SQS, but earlier
                // chunks may already have been accepted, so only fail what actually failed
                foreach (var envelope in chunks.Skip(i).SelectMany(x => x).Select(x => x.Envelope))
                {
                    failed.Add(envelope);
                }

                await reportAsync(callback, batch, units, unsendable, failed, e);
                return;
            }
        }

        await reportAsync(callback, batch, units, unsendable, failed, null);
    }

    private static async Task reportAsync(ISenderCallback callback, OutgoingMessageBatch batch,
        List<SqsSendUnit> units, List<Envelope> unsendable, HashSet<Envelope> failed, Exception? exception)
    {
        if (failed.Count == 0 && unsendable.Count == 0)
        {
            await callback.MarkSuccessfulAsync(batch);
            return;
        }

        var successes = units.Select(x => x.Envelope).Distinct().Where(x => !failed.Contains(x)).ToList();

        if (successes.Count != 0)
        {
            await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, successes));
        }

        if (failed.Count == 0) return;

        var failures = new OutgoingMessageBatch(batch.Destination, failed.ToList());

        if (exception == null)
        {
            await callback.MarkProcessingFailureAsync(failures);
        }
        else
        {
            await callback.MarkProcessingFailureAsync(failures, exception);
        }
    }

    /// <summary>
    ///     Turn one envelope into the SQS messages it needs: one normally, or N fragments when the
    ///     endpoint opted into <see cref="AmazonSqsQueue.FragmentOversizedMessages" /> and the body is
    ///     over SQS's limit. Returns <c>false</c> when this envelope can never be sent to this queue,
    ///     which the caller turns into a drop rather than a retry.
    /// </summary>
    internal bool TryBuildUnits(Envelope envelope, out SqsSendUnit[] units)
    {
        units = [];

        string body;
        try
        {
            body = _queue.Mapper!.BuildMessageBody(envelope);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while mapping envelope {Envelope} to an SQS message body for {Uri}",
                envelope, _queue.Uri);
            return false;
        }

        if (!SqsMessageFragments.ExceedsLimit(body))
        {
            units = [new SqsSendUnit(envelope, body, envelope.Id.ToString(), null)];
            return true;
        }

        // Below here the message is over SQS's limit, which SQS answers with a permanent SenderFault
        // rather than anything a retry could clear.

        if (!_queue.FragmentOversizedMessages)
        {
            _logger.LogError(
                "Envelope {Id} of message type {MessageType} produced a {Size} byte body for {Uri}, over the {Maximum} bytes Wolverine will send in one SQS message. " +
                "SQS rejects an oversized message with a permanent SenderFault, so retrying it would fail identically forever - it is being discarded instead. " +
                "Use a claim check (WolverineFx.ClaimCheck.AmazonS3), or opt this endpoint into FragmentOversizedMessages().",
                envelope.Id, envelope.MessageType, body.Length, _queue.Uri, SqsMessageFragments.MaximumBodyBytes);
            return false;
        }

        var bodies = SqsMessageFragments.Split(body);

        if (bodies.Length > SqsMessageFragments.MaximumFragments)
        {
            _logger.LogError(
                "Envelope {Id} of message type {MessageType} produced a {Size} byte body for {Uri}, which would need {Needed} fragments against a maximum of {Maximum}. " +
                "A message this large is a claim check problem rather than a framing one; see WolverineFx.ClaimCheck.AmazonS3. The message is being discarded.",
                envelope.Id, envelope.MessageType, body.Length, _queue.Uri, bodies.Length,
                SqsMessageFragments.MaximumFragments);
            return false;
        }

        units = new SqsSendUnit[bodies.Length];
        for (var i = 0; i < bodies.Length; i++)
        {
            // The fragment id is the envelope id rather than a fresh Guid, deliberately. A batch send is
            // not atomic, so a retry can re-send fragments that SQS already accepted; with a stable id the
            // stragglers are just redeliveries of the same set and the receiver folds them together,
            // where a fresh id per attempt would orphan them.
            var header = new SqsFragmentHeader(envelope.Id, i, bodies.Length);
            units[i] = new SqsSendUnit(envelope, bodies[i], $"{envelope.Id}-{i}", header);
        }

        return true;
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
    ///     <see cref="ChunkMessages" /> over the units actually being sent. Same two SQS limits, but
    ///     measured against the encoded body rather than estimated from the envelope, since by this
    ///     point the body has really been built.
    /// </summary>
    internal static SqsSendUnit[][] ChunkUnits(IEnumerable<SqsSendUnit> units)
    {
        var chunks = new List<SqsSendUnit[]>();
        var current = new List<SqsSendUnit>(10);
        var currentSize = 0;

        foreach (var unit in units)
        {
            var size = EstimateEntrySize(unit);

            if (current.Count > 0 && (current.Count == 10 || currentSize + size > MaximumBatchPayloadBytes))
            {
                chunks.Add(current.ToArray());
                current.Clear();
                currentSize = 0;
            }

            current.Add(unit);
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

    /// <summary>
    ///     Wire size of one batch entry whose body is already built. No base64 allowance here -- the
    ///     mapper has already applied whatever encoding it uses.
    /// </summary>
    internal static int EstimateEntrySize(SqsSendUnit unit)
    {
        return unit.Body.Length + EntryOverheadBytes;
    }

    // SendMessageBatchAsync is not transactional -- SQS can accept some entries and reject
    // others (throttling, oversized message, etc.) in the very same 200 response, so every
    // entry in response.Failed has to be routed back through the sender callback for retry
    private void sortChunkResults(OutgoingSqsBatch sqsBatch, SendMessageBatchResponse response,
        HashSet<Envelope> failed)
    {
        foreach (var envelope in sqsBatch.Dropped)
        {
            failed.Add(envelope);
        }

        if (response.Failed == null || response.Failed.Count == 0)
        {
            return;
        }

        foreach (var entry in response.Failed)
        {
            if (sqsBatch.TryGetEnvelope(entry.Id, out var envelope))
            {
                _logger.LogError(
                    "SQS batch send to {Uri} failed for message {Id}: {Code} - {Message} (SenderFault: {SenderFault}). The message will be retried",
                    _queue.Uri, entry.Id, entry.Code, entry.Message, entry.SenderFault);

                // One rejected fragment means the receiver can never rebuild the message, so the whole
                // envelope is a failure and every fragment of it is re-sent.
                failed.Add(envelope);
            }
            else
            {
                _logger.LogError(
                    "SQS batch send to {Uri} reported a failed entry with unrecognized Id {Id}: {Code} - {Message}",
                    _queue.Uri, entry.Id, entry.Code, entry.Message);
            }
        }
    }
}

/// <summary>
///     One SQS message on its way out. Normally one per envelope; a fragmented envelope produces one
///     per fragment, all carrying the same <see cref="Envelope" />.
/// </summary>
internal record SqsSendUnit(Envelope Envelope, string Body, string EntryId, SqsFragmentHeader? Fragment)
{
    /// <summary>
    ///     One envelope that fits in one SQS message, which is the overwhelmingly common case.
    /// </summary>
    public static SqsSendUnit Whole(AmazonSqsQueue queue, Envelope envelope)
    {
        return new SqsSendUnit(envelope, queue.Mapper!.BuildMessageBody(envelope), envelope.Id.ToString(), null);
    }
}

internal class OutgoingSqsBatch
{
    private readonly Dictionary<string, Envelope> _envelopes = new();

    public OutgoingSqsBatch(AmazonSqsQueue queue, ILogger logger, IEnumerable<SqsSendUnit> units)
    {
        var entries = new List<SendMessageBatchRequestEntry>();
        foreach (var unit in units)
        {
            var envelope = unit.Envelope;

            try
            {
                var entry = new SendMessageBatchRequestEntry(unit.EntryId, unit.Body);

                if (queue.IsFifoQueue)
                {
                    var groupId = groupIdFor(queue, unit);
                    if (groupId.IsNotEmpty())
                    {
                        entry.MessageGroupId = groupId;
                    }

                    var deduplicationId = AmazonSqsQueue.DetermineDeduplicationId(envelope);
                    if (deduplicationId.IsNotEmpty())
                    {
                        // Every fragment of one envelope would otherwise carry the identical
                        // deduplication id, and a FIFO queue would keep exactly one of them.
                        entry.MessageDeduplicationId = unit.Fragment is { } fragment
                            ? $"{deduplicationId}-{fragment.Index}"
                            : deduplicationId;
                    }
                }
                else if (queue.EnableFairQueueMessageGroups)
                {
                    // SQS fair queues: a MessageGroupId on a standard queue improves tenant fairness.
                    // No deduplication semantics apply to standard queues. See GH-2886.
                    var groupId = groupIdFor(queue, unit);
                    if (groupId.IsNotEmpty())
                    {
                        entry.MessageGroupId = groupId;
                    }
                }

                foreach (var attribute in queue.Mapper!.ToAttributes(envelope))
                {
                    entry.MessageAttributes ??= new Dictionary<string, MessageAttributeValue>();
                    entry.MessageAttributes.Add(attribute.Key, attribute.Value);
                }

                if (unit.Fragment is { } header)
                {
                    entry.MessageAttributes ??= new Dictionary<string, MessageAttributeValue>();
                    foreach (var pair in SqsMessageFragments.AttributesFor(header.FragmentId, header.Index,
                                 header.Count))
                    {
                        entry.MessageAttributes[pair.Key] = pair.Value;
                    }
                }

                var delaySeconds = queue.NativeDelaySecondsFor(envelope, DateTimeOffset.UtcNow, logger);
                if (delaySeconds > 0)
                {
                    entry.DelaySeconds = delaySeconds;
                }

                entries.Add(entry);
                _envelopes[entry.Id] = envelope;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while mapping envelope {Envelope} to an SQS SendMessageBatchRequestEntry",
                    envelope);

                Dropped.Add(envelope);
            }
        }

        Request = new SendMessageBatchRequest(queue.QueueUrl, entries);
    }

    /// <summary>
    ///     Envelopes that could not be turned into an SQS entry at all. They are not in the request, so
    ///     they cannot succeed, and leaving them out of both outcomes would silently lose them.
    /// </summary>
    public List<Envelope> Dropped { get; } = [];

    public SendMessageBatchRequest Request { get; }

    public bool TryGetEnvelope(string id, out Envelope envelope)
    {
        return _envelopes.TryGetValue(id, out envelope!);
    }

    /// <summary>
    ///     Fragments of one message must share a group id, so that a FIFO queue keeps the whole set on a
    ///     single consumer and in order. Returns null when there is no group id to set, which leaves
    ///     <c>MessageGroupId</c> unset exactly as before.
    /// </summary>
    private static string? groupIdFor(AmazonSqsQueue queue, SqsSendUnit unit)
    {
        var groupId = unit.Fragment is { } fragment
            ? SqsMessageFragments.GroupIdFor(unit.Envelope, fragment.FragmentId)
            : queue.Mapper!.DetermineGroupId(unit.Envelope);

        return groupId.IsNotEmpty() ? groupId : null;
    }
}
