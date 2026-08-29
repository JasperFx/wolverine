using Dekaf;
using Dekaf.Consumer;
using Dekaf.Protocol;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Executes a bounded, one-shot Kafka replay (GH-3147): a separate manually-assigned consumer (no
/// consumer group, no commits) seeks to the requested start on each partition, reads forward to the
/// end boundary, and feeds every record through the normal Wolverine handler pipeline. The live
/// consumer group's committed offsets are never touched.
/// </summary>
internal sealed class KafkaReplay
{
    private static readonly TimeSpan _metadataTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _pollTimeout = TimeSpan.FromSeconds(1);

    private readonly KafkaTransport _transport;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;

    public KafkaReplay(KafkaTransport transport, IWolverineRuntime runtime)
    {
        _transport = transport;
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<KafkaReplay>();
    }

    public async Task<KafkaReplayResult> ExecuteAsync(KafkaReplayRequest request, CancellationToken token)
    {
        if (request.FromOffset.HasValue && request.FromTimestamp.HasValue)
            throw new ArgumentException("Specify only one of FromOffset or FromTimestamp", nameof(request));
        if (request.ToOffset.HasValue && request.ToTimestamp.HasValue)
            throw new ArgumentException("Specify only one of ToOffset or ToTimestamp", nameof(request));

        var topic = _transport.Topics[request.Topic];
        var mapper = topic.EnsureEnvelopeMapper(_runtime);
        var messageTypeName = topic.MessageType?.ToMessageTypeName();
        var pipeline = _runtime.Pipeline;
        var callback = new ReplayChannelCallback();

        // The Destination stamp below has to imitate whichever endpoint actually listens to this topic,
        // which is not always the topic itself.
        var liveEndpoint = LiveListeningEndpointFor(topic, _transport);

        // Manual-assignment consumer: no consumer group at all, no commits, no offset store.
        var config = _transport.ConsumerConfig.Clone();
        config.GroupId = null;
        config.GroupInstanceId = null;
        config.EnableAutoCommit = false;
        config.EnableAutoOffsetStore = false;

        var consumer = _transport.CreateConsumer(config);
        await using var _ = consumer;
        await consumer.InitializeAsync(token);

        var partitions = await ResolvePartitions(request);
        var plan = await BuildPlan(consumer, request, partitions, token);

        var toAssign = plan.Where(x => x.Start < x.End)
            .Select(x => new TopicPartitionOffset(x.Partition.Topic, x.Partition.Partition, x.Start)).ToList();

        if (toAssign.Count == 0)
        {
            return new KafkaReplayResult { RecordsReplayed = 0, PartitionsReplayed = 0 };
        }

        // Assign at the requested start offsets in one call
        consumer.Partitions.IncrementalAssign(toAssign);

        var ends = plan.Where(x => x.Start < x.End).ToDictionary(x => x.Partition, x => x.End);
        var remaining = new HashSet<TopicPartition>(ends.Keys);
        long replayed = 0;

        _logger.LogInformation("Starting Kafka replay of topic {Topic} across {Count} partition(s)",
            request.Topic, remaining.Count);

        try
        {
            while (remaining.Count > 0 && !token.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? maybe;
                try
                {
                    maybe = await consumer.ConsumeOneAsync(_pollTimeout, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (maybe == null)
                {
                    continue;
                }

                var result = maybe.Value;
                var tp = new TopicPartition(result.Topic, result.Partition);
                if (!remaining.Contains(tp))
                {
                    continue;
                }

                if (result.Offset >= ends[tp])
                {
                    // Reached this partition's end boundary; stop pulling from it.
                    remaining.Remove(tp);
                    consumer.Partitions.Pause([tp]);
                    continue;
                }

                var envelope = mapper.CreateEnvelope(result);
                envelope.TopicName = result.Topic;
                envelope.Offset = result.Offset;
                envelope.PartitionId = result.Partition;
                envelope.MessageType ??= messageTypeName;

                // The live receivers stamp Destination when they accept an envelope, and the pipeline relies
                // on it: the executor logs it after every handler run and dead-lettering keys the inbox row
                // on it. Without it each replayed record threw a NullReferenceException after its handler
                // succeeded, and one whose handler failed could not be dead-lettered.
                //
                // Envelope.MarkReceived stamps the *listener's* Address, which for a topic consumed through
                // ListenToKafkaTopics(...) is the group endpoint's Uri rather than the topic's -- so this has
                // to follow that endpoint, or the dead letter row, the metrics tag and the endpoint the
                // pipeline resolves from Destination all name something the live path never names.
                envelope.Destination ??= liveEndpoint.Uri;

                await pipeline.InvokeAsync(envelope, callback);
                replayed++;

                if (result.Offset + 1 >= ends[tp])
                {
                    remaining.Remove(tp);
                    consumer.Partitions.Pause([tp]);
                }
            }
        }
        finally
        {
            // Close without committing — the live group's offsets are untouched.
            await consumer.CloseAsync(CancellationToken.None);
        }

        _logger.LogInformation("Finished Kafka replay of topic {Topic}: {Count} record(s) replayed",
            request.Topic, replayed);

        return new KafkaReplayResult { RecordsReplayed = replayed, PartitionsReplayed = ends.Count };
    }

    /// <summary>
    /// The endpoint whose live listener would have received this topic's records. Normally the topic
    /// itself, but <c>ListenToKafkaTopics(...)</c> keeps both the consumer config and the listener's
    /// Address on the <see cref="KafkaTopicGroup" /> rather than on the individual topics, so a topic
    /// consumed that way has to imitate the group instead.
    /// </summary>
    internal static KafkaTopic LiveListeningEndpointFor(KafkaTopic topic, KafkaTransport transport)
    {
        // A topic under its own consumer group is its own listener. GetEffectiveConsumerConfig() fills this
        // in from the transport when the listener is built, so after bootstrap it is set for every topic
        // that really does listen -- and null for one that only gets replayed.
        if (topic is KafkaTopicGroup || !string.IsNullOrEmpty(topic.ConsumerConfig?.GroupId))
        {
            return topic;
        }

        return transport.TopicGroups.FirstOrDefault(x => x.TopicNames.Contains(topic.TopicName)) ?? topic;
    }

    private async Task<List<TopicPartition>> ResolvePartitions(KafkaReplayRequest request)
    {
        await using var admin = _transport.CreateAdminClient();
        using var timeout = new CancellationTokenSource(_metadataTimeout);

        var described = await admin.DescribeTopicsAsync([request.Topic], timeout.Token);
        if (!described.TryGetValue(request.Topic, out var topicDescription)
            || topicDescription.ErrorCode != ErrorCode.None)
        {
            throw new InvalidOperationException(
                $"Unable to read metadata for Kafka topic '{request.Topic}': {(described.TryGetValue(request.Topic, out var d) ? d.ErrorCode.ToString() : "not found")}");
        }

        var ids = topicDescription.Partitions.Select(x => x.PartitionIndex);
        if (request.Partitions is { Length: > 0 })
        {
            var wanted = request.Partitions.ToHashSet();
            ids = ids.Where(wanted.Contains);
        }

        return ids.Select(id => new TopicPartition(request.Topic, id)).ToList();
    }

    private static async Task<List<PartitionPlan>> BuildPlan(IKafkaConsumer<string, byte[]> consumer,
        KafkaReplayRequest request, List<TopicPartition> partitions, CancellationToken token)
    {
        var startTimes = request.FromTimestamp.HasValue
            ? await OffsetsForTimes(consumer, partitions, request.FromTimestamp.Value, token)
            : null;
        var endTimes = request.ToTimestamp.HasValue
            ? await OffsetsForTimes(consumer, partitions, request.ToTimestamp.Value, token)
            : null;

        var plan = new List<PartitionPlan>();
        foreach (var tp in partitions)
        {
            var watermarks = await consumer.Offsets.QueryWatermarkOffsetsAsync(tp, token);
            var low = watermarks.Low;
            var high = watermarks.High;

            long start;
            if (request.FromOffset.HasValue)
            {
                start = Math.Clamp(request.FromOffset.Value, low, high);
            }
            else if (startTimes != null)
            {
                var resolved = startTimes.TryGetValue(tp, out var s) ? s : -1;
                start = resolved < 0 ? high : Math.Clamp(resolved, low, high);
            }
            else
            {
                start = low;
            }

            long end;
            if (request.ToOffset.HasValue)
            {
                end = Math.Clamp(request.ToOffset.Value, low, high);
            }
            else if (endTimes != null)
            {
                var resolved = endTimes.TryGetValue(tp, out var e) ? e : -1;
                end = resolved < 0 ? high : Math.Clamp(resolved, low, high);
            }
            else
            {
                end = high;
            }

            plan.Add(new PartitionPlan(tp, start, end));
        }

        return plan;
    }

    private static async Task<IReadOnlyDictionary<TopicPartition, long>> OffsetsForTimes(
        IKafkaConsumer<string, byte[]> consumer, List<TopicPartition> partitions, DateTimeOffset timestamp,
        CancellationToken token)
    {
        var request = partitions
            .Select(tp => new TopicPartitionTimestamp(tp, timestamp))
            .ToList();

        return await consumer.Offsets.GetOffsetsForTimesAsync(request, token);
    }

    private sealed record PartitionPlan(TopicPartition Partition, long Start, long End);

    private sealed class ReplayChannelCallback : IChannelCallback
    {
        public IHandlerPipeline? Pipeline => null;
        public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
    }
}
