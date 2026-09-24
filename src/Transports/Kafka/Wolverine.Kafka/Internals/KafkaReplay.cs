using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Executes a bounded, one-shot Kafka replay (GH-3147): a separate <c>Assign()</c>-based consumer (with a
/// unique throwaway group id and no commits) seeks to the requested start on each partition, reads
/// forward to the end boundary, and feeds every record through the normal Wolverine handler pipeline. The
/// live consumer group's committed offsets are never touched.
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

        // Throwaway, Assign-only consumer: unique group sharing the live group's prefix, no commits, no
        // offset store.
        var config = new ConsumerConfig(_transport.ConsumerConfig)
        {
            GroupId = ReplayGroupIdFor(topic, _transport, _runtime.Options.ServiceName),
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        };

        using var consumer = _transport.CreateConsumer(config);

        var partitions = ResolvePartitions(request);
        var plan = BuildPlan(consumer, request, partitions);

        var toAssign = plan.Where(x => x.Start < x.End)
            .Select(x => new TopicPartitionOffset(x.Partition, new Offset(x.Start))).ToList();

        if (toAssign.Count == 0)
        {
            return new KafkaReplayResult { RecordsReplayed = 0, PartitionsReplayed = 0 };
        }

        consumer.Assign(toAssign);

        var ends = plan.Where(x => x.Start < x.End).ToDictionary(x => x.Partition, x => x.End);
        var remaining = new HashSet<TopicPartition>(ends.Keys);
        long replayed = 0;

        _logger.LogInformation("Starting Kafka replay of topic {Topic} across {Count} partition(s)",
            request.Topic, remaining.Count);

        try
        {
            while (remaining.Count > 0 && !token.IsCancellationRequested)
            {
                var result = consumer.Consume(_pollTimeout);
                if (result?.Message == null)
                {
                    continue;
                }

                var tp = result.TopicPartition;
                if (!remaining.Contains(tp))
                {
                    continue;
                }

                if (result.Offset.Value >= ends[tp])
                {
                    // Reached this partition's end boundary; stop pulling from it.
                    remaining.Remove(tp);
                    consumer.Pause([tp]);
                    continue;
                }

                var envelope = mapper.CreateEnvelope(result.Topic, result.Message);
                envelope.TopicName = result.Topic;
                envelope.Offset = result.Offset.Value;
                envelope.PartitionId = result.Partition.Value;
                envelope.MessageType ??= messageTypeName;

                // The live receivers stamp Destination when they accept an envelope, and the pipeline relies
                // on it: the executor logs it after every handler run and dead-lettering keys the inbox row
                // on it. Without it each replayed record threw a NullReferenceException after its handler
                // succeeded, and one whose handler failed could not be dead-lettered.
                envelope.Destination ??= topic.Uri;

                await pipeline.InvokeAsync(envelope, callback);
                replayed++;

                if (result.Offset.Value + 1 >= ends[tp])
                {
                    remaining.Remove(tp);
                    consumer.Pause([tp]);
                }
            }
        }
        finally
        {
            // Close without committing — the live group's offsets are untouched.
            consumer.Close();
        }

        _logger.LogInformation("Finished Kafka replay of topic {Topic}: {Count} record(s) replayed",
            request.Topic, replayed);

        return new KafkaReplayResult { RecordsReplayed = replayed, PartitionsReplayed = ends.Count };
    }

    /// <summary>
    /// The throwaway replay group is named after the group the topic's live listener consumes under —
    /// the topic's own group id, else the transport's, else the service name. Brokers commonly grant
    /// consumer groups by prefix (Confluent Cloud ACLs are the usual case), and the live group is the
    /// one prefix the application is known to hold; the service name alone defaults to the entry
    /// assembly name, which such an ACL refuses with "Group authorization failed".
    /// </summary>
    internal static string ReplayGroupIdFor(KafkaTopic topic, KafkaTransport transport, string serviceName)
    {
        var liveGroupId = topic.ConsumerConfig?.GroupId;
        if (string.IsNullOrEmpty(liveGroupId))
        {
            liveGroupId = transport.ConsumerConfig.GroupId;
        }

        if (string.IsNullOrEmpty(liveGroupId))
        {
            liveGroupId = serviceName;
        }

        return $"{liveGroupId}-replay-{Guid.NewGuid():N}";
    }

    private List<TopicPartition> ResolvePartitions(KafkaReplayRequest request)
    {
        using var admin = _transport.CreateAdminClient();
        var metadata = admin.GetMetadata(request.Topic, _metadataTimeout);
        var topicMetadata = metadata.Topics.SingleOrDefault(x => x.Topic == request.Topic);
        if (topicMetadata == null || topicMetadata.Error.IsError)
        {
            throw new InvalidOperationException(
                $"Unable to read metadata for Kafka topic '{request.Topic}': {topicMetadata?.Error.Reason ?? "not found"}");
        }

        var ids = topicMetadata.Partitions.Select(x => x.PartitionId);
        if (request.Partitions is { Length: > 0 })
        {
            var wanted = request.Partitions.ToHashSet();
            ids = ids.Where(wanted.Contains);
        }

        return ids.Select(id => new TopicPartition(request.Topic, new Partition(id))).ToList();
    }

    private List<PartitionPlan> BuildPlan(IConsumer<string, byte[]> consumer, KafkaReplayRequest request,
        List<TopicPartition> partitions)
    {
        var startTimes = request.FromTimestamp.HasValue
            ? OffsetsForTimes(consumer, partitions, request.FromTimestamp.Value)
            : null;
        var endTimes = request.ToTimestamp.HasValue
            ? OffsetsForTimes(consumer, partitions, request.ToTimestamp.Value)
            : null;

        var plan = new List<PartitionPlan>();
        foreach (var tp in partitions)
        {
            var watermarks = consumer.QueryWatermarkOffsets(tp, _metadataTimeout);
            var low = watermarks.Low.Value;
            var high = watermarks.High.Value;

            long start;
            if (request.FromOffset.HasValue)
            {
                start = Math.Clamp(request.FromOffset.Value, low, high);
            }
            else if (startTimes != null)
            {
                var resolved = startTimes[tp];
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
                var resolved = endTimes[tp];
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

    private static Dictionary<TopicPartition, long> OffsetsForTimes(IConsumer<string, byte[]> consumer,
        List<TopicPartition> partitions, DateTimeOffset timestamp)
    {
        var request = partitions
            .Select(tp => new TopicPartitionTimestamp(tp, new Timestamp(timestamp.UtcDateTime, TimestampType.CreateTime)))
            .ToList();

        var resolved = consumer.OffsetsForTimes(request, _metadataTimeout);
        return resolved.ToDictionary(x => x.TopicPartition, x => x.Offset.Value);
    }

    private sealed record PartitionPlan(TopicPartition Partition, long Start, long End);

    private sealed class ReplayChannelCallback : IChannelCallback
    {
        public IHandlerPipeline? Pipeline => null;
        public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
    }
}
