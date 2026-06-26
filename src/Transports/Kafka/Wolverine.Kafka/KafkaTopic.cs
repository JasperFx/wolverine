using Confluent.Kafka;
using Confluent.Kafka.Admin;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using System.Text;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka;

public class KafkaTopic : Endpoint<IKafkaEnvelopeMapper, KafkaEnvelopeMapper>, IBrokerEndpoint
{
    // Strictly an identifier for the endpoint
    public const string WolverineTopicsName = "wolverine.topics";

    [IgnoreDescription]
    public KafkaTransport Parent { get; }

    public KafkaTopic(KafkaTransport parent, string topicName, EndpointRole role) : base(new Uri($"{parent.Protocol}://topic/" + topicName), role)
    {
        Parent = parent;
        EndpointName = topicName;
        TopicName = topicName;
        BrokerRole = "topic";

        Specification.Name = topicName;
    }

    protected override KafkaEnvelopeMapper buildMapper(IWolverineRuntime runtime)
    {
        return new KafkaEnvelopeMapper(this);
    }

    public override bool AutoStartSendingAgent()
    {
        return true;
    }

    [ChildDescription]
    public TopicSpecification Specification { get; } = new();

    public string TopicName { get; }

    /// <summary>
    /// Override for this specific Kafka Topic
    /// </summary>
    [ChildDescription]
    public ConsumerConfig? ConsumerConfig { get; internal set; }

    /// <summary>
    /// Override for this specific Kafka Topic
    /// </summary>
    [ChildDescription]
    public ProducerConfig? ProducerConfig { get; internal set; }

    /// <summary>
    /// How this listener commits consumer offsets back to Kafka. Defaults to
    /// <see cref="Kafka.CommitMode.StoreThenAutoFlush"/> — the non-blocking, idiomatic high-throughput
    /// model. See GH-3150. Inherited by <see cref="KafkaTopicGroup"/>.
    /// </summary>
    public CommitMode CommitMode { get; set; } = CommitMode.StoreThenAutoFlush;

    /// <summary>
    /// Number of completed messages between commits when <see cref="CommitMode"/> is
    /// <see cref="Kafka.CommitMode.BatchCount"/>. Default 100.
    /// </summary>
    public int CommitBatchCount { get; set; } = 100;

    /// <summary>
    /// Minimum elapsed time between commits when <see cref="CommitMode"/> is
    /// <see cref="Kafka.CommitMode.BatchInterval"/>. Default 5 seconds.
    /// </summary>
    public TimeSpan CommitBatchInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// True when static group membership was requested for this specific listener (GH-3139).
    /// Inherited by <see cref="KafkaTopicGroup"/>.
    /// </summary>
    internal bool StaticMembershipRequested { get; set; }

    /// <summary>
    /// True for an ephemeral "hot-tail" listener (GH-3146): a unique per-process consumer group +
    /// AutoOffsetReset.Latest so every node tails live and receives all messages, never committing or
    /// replaying. Inherited by <see cref="KafkaTopicGroup"/>.
    /// </summary>
    internal bool IsHotTail { get; set; }

    /// <summary>
    /// True when intra-partition by-key concurrency is enabled (GH-3140): the incoming Kafka message key
    /// is stamped as the envelope's grouping key so same-key messages process sequentially and different
    /// keys process concurrently across the sharded execution slots. Inherited by <see cref="KafkaTopicGroup"/>.
    /// </summary>
    internal bool GroupByMessageKey { get; set; }

    /// <summary>
    /// When set, this is a non-blocking retry-tier topic (GH-3148): the listener waits this fixed delay
    /// (relative to each record's produced timestamp) before reprocessing the record through the normal
    /// handler pipeline. A re-failure escalates to the next tier; the last tier exhausts to the DLQ.
    /// </summary>
    internal TimeSpan? RetryTierDelay { get; set; }

    /// <summary>
    /// Enable native dead letter queue support for this endpoint.
    /// When enabled, failed messages will be produced to the Kafka DLQ topic
    /// instead of being moved to database-backed dead letter storage.
    /// Default is false (opt-in).
    /// </summary>
    public bool NativeDeadLetterQueueEnabled { get; set; }

    // Inherited by KafkaTopicGroup, which shares the same NativeDeadLetterQueueEnabled flag.
    public override DeadLetterStorageMode DeadLetterStorage => NativeDeadLetterQueueEnabled
        ? DeadLetterStorageMode.Native
        : DeadLetterStorageMode.Durable;

    /// <summary>
    /// When true, the Kafka consumer group ID will be stamped onto the incoming
    /// envelope's GroupId property. Useful when you want the consumer group name
    /// to be available as envelope metadata for routing or correlation purposes.
    /// Default is true.
    /// </summary>
    public bool StampConsumerGroupIdOnEnvelope { get; set; } = true;

    /// <summary>
    /// When true, Wolverine will not attempt to create or delete this topic
    /// during transport startup or resource teardown, even when AutoProvision
    /// is enabled on the parent transport. Use this for topics owned by an
    /// external system where the calling identity lacks CreateTopics or
    /// DeleteTopics ACLs. Default is false.
    /// </summary>
    public bool IsExternallyOwned { get; set; }

    public static string TopicNameForUri(Uri uri)
    {
        return uri.Segments.Last().Trim('/');
    }

    /// <summary>
    /// Gets the effective ConsumerConfig for this topic, ensuring BootstrapServers is inherited from parent if not set
    /// </summary>
    internal ConsumerConfig GetEffectiveConsumerConfig()
    {
        if (ConsumerConfig != null && string.IsNullOrEmpty(ConsumerConfig.BootstrapServers))
        {
            ConsumerConfig.BootstrapServers = Parent.ConsumerConfig.BootstrapServers;
        }

        if (ConsumerConfig != null && string.IsNullOrEmpty(ConsumerConfig.GroupId))
        {
            ConsumerConfig.GroupId = Parent.ConsumerConfig.GroupId;
        }

        return ConsumerConfig ?? Parent.ConsumerConfig;
    }

    /// <summary>
    /// Gets the effective ProducerConfig for this topic, ensuring BootstrapServers is inherited from parent if not set
    /// </summary>
    internal ProducerConfig GetEffectiveProducerConfig()
    {
        if (ProducerConfig != null && string.IsNullOrEmpty(ProducerConfig.BootstrapServers))
        {
            ProducerConfig.BootstrapServers = Parent.ProducerConfig.BootstrapServers;
        }

        return ProducerConfig ?? Parent.ProducerConfig;
    }

    /// <summary>
    /// Ensure the envelope mapper has been built (e.g. for a one-shot replay of a topic that isn't a
    /// configured live listener). See GH-3147.
    /// </summary>
    internal IKafkaEnvelopeMapper EnsureEnvelopeMapper(IWolverineRuntime runtime)
    {
        return EnvelopeMapper ??= BuildMapper(runtime);
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        EnvelopeMapper ??= BuildMapper(runtime);

        var config = GetEffectiveConsumerConfig();

        ApplyHotTailConfig(config, runtime);

        // Wire the Kafka client for the configured commit strategy (GH-3150). Replaces the previous
        // blanket EnableAutoCommit=false for Durable mode — the default StoreThenAutoFlush mode relies
        // on Kafka's background committer flushing manually stored offsets.
        KafkaOffsetCommitter.ApplyTo(config, CommitMode);

        var listener = new KafkaListener(this, config,
            Parent.CreateConsumer(config), receiver, runtime.LoggerFactory.CreateLogger<KafkaListener>());
        return ValueTask.FromResult((IListener)listener);
    }

    /// <summary>
    /// For an ephemeral hot-tail listener (GH-3146), assign a unique per-process consumer group so every
    /// node receives all messages and never replays, and disable Wolverine-managed commits (the
    /// position is throwaway). Setting EnableAutoCommit=true makes the commit strategy hands-off.
    /// </summary>
    private protected void ApplyHotTailConfig(ConsumerConfig config, IWolverineRuntime runtime)
    {
        if (!IsHotTail)
        {
            return;
        }

        config.GroupId = $"{runtime.Options.ServiceName}-hot-tail-{Guid.NewGuid():N}";
        config.EnableAutoCommit = true;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        EnvelopeMapper ??= BuildMapper(runtime);
        
        return Mode == EndpointMode.Inline
            ? new InlineKafkaSender(this)
            : new BatchedSender(this, new KafkaSenderProtocol(this), runtime.Cancellation,
                runtime.LoggerFactory.CreateLogger<KafkaSenderProtocol>());
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        if (NativeDeadLetterQueueEnabled)
        {
            var dlqTopic = Parent.Topics[Parent.DeadLetterQueueTopicName];
            dlqTopic.EnvelopeMapper ??= dlqTopic.BuildMapper(runtime);
            deadLetterSender = new InlineKafkaSender(dlqTopic, fixedDestination: true);
            return true;
        }

        deadLetterSender = default;
        return false;
    }

    public async ValueTask<bool> CheckAsync()
    {
        // Can't do anything about this
        if (Parent.Usage == KafkaUsage.ConsumeOnly) return true;

        if (TopicName == WolverineTopicsName) return true; // don't care, this is just a marker
        try
        {
            using var client = Parent.CreateProducer(GetEffectiveProducerConfig());
            await client.ProduceAsync(TopicName, new Message<string, byte[]>
            {
                Key = "ping",
                Value = Encoding.UTF8.GetBytes("ping")
            });


            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        if (TopicName == WolverineTopicsName) return; // don't care, this is just a marker

        if (IsExternallyOwned) return;

        using var adminClient = Parent.CreateAdminClient();
        await adminClient.DeleteTopicsAsync([TopicName]);
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        if (TopicName == WolverineTopicsName) return; // don't care, this is just a marker

        if (IsExternallyOwned) return;

        using var adminClient = Parent.CreateAdminClient();
        Specification.Name = TopicName;

        try
        {
            await CreateTopicFunc(adminClient, this);

            logger.LogInformation("Created Kafka topic {Topic}", TopicName);
        }
        catch (CreateTopicsException e)
        {
            if (e.Message.Contains("already exists.")) return;
            throw;
        }
    }

    /// <summary>
    /// Called during transport startup. When AutoProvision is on for the parent
    /// transport, ensure the Kafka topic exists on the broker before listeners or
    /// senders try to use it. Topics marked <see cref="IsExternallyOwned"/> are
    /// skipped so externally-managed topics don't fail startup when the calling
    /// identity lacks CreateTopics ACLs.
    /// See https://github.com/JasperFx/wolverine/issues/2537.
    /// </summary>
    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (Parent.AutoProvision && !IsExternallyOwned)
        {
            await SetupAsync(logger);
        }
    }

    /// <summary>
    /// Override how this Kafka topic is created
    /// </summary>
    [IgnoreDescription]
    public Func<IAdminClient, KafkaTopic, Task> CreateTopicFunc { get; internal set; } = (c, t) => c.CreateTopicsAsync([t.Specification]);
}

public enum QualityOfService
{
    /// <summary>
    /// "At least once" delivery guarantee by auto-ack'ing incoming messages
    /// </summary>
    AtLeastOnce,

    /// <summary>
    /// "At most once" delivery guarantee by trying to ack received messages before processing
    /// </summary>
    AtMostOnce
}