using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar.Internal;

/// <summary>
/// Executes a bounded, one-shot Pulsar replay (GH-3184): a throwaway, non-durable <c>Reader</c> cursor
/// reads forward from the start bound and feeds every message in the requested window through the normal
/// Wolverine handler pipeline. The Reader owns its own auto-generated, non-durable subscription, so the
/// live durable subscription's cursor is never touched.
/// </summary>
internal sealed class PulsarReplay
{
    private readonly PulsarTransport _transport;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;

    public PulsarReplay(PulsarTransport transport, IWolverineRuntime runtime)
    {
        _transport = transport;
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<PulsarReplay>();
    }

    public async Task<PulsarReplayResult> ExecuteAsync(PulsarReplayRequest request, CancellationToken token)
    {
        if (request.FromMessageId is not null && request.FromTimestamp.HasValue)
            throw new ArgumentException("Specify only one of FromMessageId or FromTimestamp", nameof(request));
        if (request.ToMessageId is not null && request.ToTimestamp.HasValue)
            throw new ArgumentException("Specify only one of ToMessageId or ToTimestamp", nameof(request));

        var endpoint = _transport.EndpointFor(request.Topic);
        var destination = endpoint.Uri;
        var topic = endpoint.PulsarTopic();
        var mapper = endpoint.BuildMapper(_runtime);
        var pipeline = _runtime.Pipeline;
        var callback = new ReplayChannelCallback();

        // Reader at Earliest with its own non-durable, auto-named cursor: never touches the live
        // durable subscription. We filter the requested window in code rather than seeking so the
        // From/To bounds are inclusive and unambiguous.
        var reader = _transport.Client!.NewReader()
            .Topic(topic)
            .StartMessageId(MessageId.Earliest)
            .Create();

        try
        {
            // Snapshot the topic's current end per partition so the replay stops at "now" and never
            // tails traffic published after it began. An empty topic yields only sentinel ids.
            var endByPartition = (await reader.GetLastMessageIds(token))
                .Where(x => !IsSentinel(x))
                .ToDictionary(x => x.Partition, x => x);

            if (endByPartition.Count == 0)
            {
                _logger.LogInformation("Pulsar replay of topic {Topic}: topic is empty, nothing to replay", topic);
                return new PulsarReplayResult { MessagesReplayed = 0 };
            }

            var remaining = new HashSet<int>(endByPartition.Keys);
            long replayed = 0;

            _logger.LogInformation("Starting Pulsar replay of topic {Topic} across {Count} partition(s)",
                topic, remaining.Count);

            await foreach (var message in reader.Messages(token))
            {
                var id = message.MessageId;
                if (remaining.Contains(id.Partition))
                {
                    var atEnd = id.CompareTo(endByPartition[id.Partition]) >= 0;

                    if (ShouldReplay(request, message, id, out var pastEnd))
                    {
                        var envelope = new PulsarEnvelope(message) { Data = message.Data.ToArray() };
                        mapper.MapIncomingToEnvelope(envelope, message);

                        // The replay bypasses a real listener (and so the IListener-driven
                        // Envelope.MarkReceived), so stamp the Destination the pipeline expects for an
                        // incoming envelope — the listener address for this endpoint.
                        envelope.Destination = destination;

                        await pipeline.InvokeAsync(envelope, callback);
                        replayed++;
                    }

                    if (atEnd || pastEnd)
                    {
                        // This partition has reached its snapshot end (or run past the requested upper
                        // bound); stop pulling from it.
                        remaining.Remove(id.Partition);
                    }
                }

                // Break the instant every partition is done — within this iteration, *before* calling
                // the reader's MoveNextAsync again. DotPulsar's Messages() async-enumerable has no poll
                // timeout, so a top-of-loop check would block forever waiting for a message past the
                // snapshot end that never arrives.
                if (remaining.Count == 0)
                {
                    break;
                }
            }

            _logger.LogInformation("Finished Pulsar replay of topic {Topic}: {Count} message(s) replayed",
                topic, replayed);

            return new PulsarReplayResult { MessagesReplayed = replayed };
        }
        finally
        {
            await reader.DisposeAsync();
        }
    }

    /// <summary>
    /// Apply the request's start/end window to a single message. Sets <paramref name="pastUpperBound"/>
    /// when the message is beyond the requested upper bound so the caller can stop reading the partition.
    /// </summary>
    private static bool ShouldReplay(PulsarReplayRequest request, IMessage<ReadOnlySequence<byte>> message,
        MessageId id, out bool pastUpperBound)
    {
        pastUpperBound = false;

        // Upper bound (inclusive): once exceeded, this partition is done.
        if (request.ToMessageId is not null && id.CompareTo(request.ToMessageId) > 0)
        {
            pastUpperBound = true;
            return false;
        }

        if (request.ToTimestamp.HasValue && PublishTime(message) > request.ToTimestamp.Value)
        {
            pastUpperBound = true;
            return false;
        }

        // Lower bound (inclusive): skip anything before the requested start.
        if (request.FromMessageId is not null && id.CompareTo(request.FromMessageId) < 0)
        {
            return false;
        }

        if (request.FromTimestamp.HasValue && PublishTime(message) < request.FromTimestamp.Value)
        {
            return false;
        }

        return true;
    }

    private static DateTimeOffset PublishTime(IMessage<ReadOnlySequence<byte>> message)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds((long)message.PublishTime);
    }

    /// <summary>
    /// True for the <see cref="MessageId.Earliest"/> / <see cref="MessageId.Latest"/> sentinels and the
    /// "no messages yet" id Pulsar returns for an empty topic — all of which carry <c>ulong.MaxValue</c>
    /// ledger/entry components rather than a real position.
    /// </summary>
    private static bool IsSentinel(MessageId id)
    {
        return id is null || id.LedgerId == ulong.MaxValue || id.EntryId == ulong.MaxValue;
    }

    private sealed class ReplayChannelCallback : IChannelCallback
    {
        public IHandlerPipeline? Pipeline => null;
        public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
    }
}
