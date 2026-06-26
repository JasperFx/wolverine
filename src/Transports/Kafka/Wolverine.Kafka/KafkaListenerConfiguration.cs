using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka;

public class KafkaListenerConfiguration : InteroperableListenerConfiguration<KafkaListenerConfiguration, KafkaTopic, IKafkaEnvelopeMapper, KafkaEnvelopeMapper>
{
    public KafkaListenerConfiguration(KafkaTopic endpoint) : base(endpoint)
    {
    }

    public KafkaListenerConfiguration(Func<KafkaTopic> source) : base(source)
    {
    }
    
    /// <summary>
    /// Fine tune the TopicSpecification for this Kafka Topic if it is being created by Wolverine
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public KafkaListenerConfiguration Specification(Action<TopicSpecification> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        add(topic => configure(topic.Specification));
        return this;
    }

    /// <summary>
    /// Choose how this listener commits consumer offsets back to Kafka. Defaults to
    /// <see cref="CommitMode.StoreThenAutoFlush"/> (non-blocking, idiomatic high throughput). See GH-3150.
    /// </summary>
    public KafkaListenerConfiguration CommitOffsets(CommitMode mode)
    {
        add(topic => topic.CommitMode = mode);
        return this;
    }

    /// <summary>
    /// Have Wolverine commit the contiguous offset watermark after every <paramref name="count"/>
    /// successfully processed messages. Never commits ahead of the lowest in-flight offset.
    /// </summary>
    public KafkaListenerConfiguration CommitOffsetsAfterCount(int count)
    {
        add(topic =>
        {
            topic.CommitMode = CommitMode.BatchCount;
            topic.CommitBatchCount = count;
        });
        return this;
    }

    /// <summary>
    /// Have Wolverine commit the contiguous offset watermark once at least <paramref name="interval"/>
    /// has elapsed since the previous commit. Never commits ahead of the lowest in-flight offset.
    /// </summary>
    public KafkaListenerConfiguration CommitOffsetsAfterInterval(TimeSpan interval)
    {
        add(topic =>
        {
            topic.CommitMode = CommitMode.BatchInterval;
            topic.CommitBatchInterval = interval;
        });
        return this;
    }

    /// <summary>
    /// Opt this listener's consumer into cooperative-sticky rebalancing
    /// (<c>partition.assignment.strategy = CooperativeSticky</c>) so a rebalance keeps unaffected
    /// partitions instead of revoking everything. Opt-in. See GH-3139. Call after
    /// <see cref="ConfigureConsumer"/> if you also use that (it replaces the whole consumer config).
    /// </summary>
    public KafkaListenerConfiguration UseCooperativeStickyAssignment()
    {
        add(topic =>
        {
            topic.ConsumerConfig ??= new ConsumerConfig();
            topic.ConsumerConfig.PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky;
        });
        return this;
    }

    /// <summary>
    /// Enable Kafka static group membership (<c>group.instance.id</c>) for this listener so rolling
    /// restarts of the same node don't churn partitions. The id is resolved from
    /// <paramref name="instanceId"/> if supplied, otherwise from <c>POD_NAME</c>, then <c>HOSTNAME</c>,
    /// then the machine name. Must be unique per node and stable across restarts. See GH-3139.
    /// </summary>
    public KafkaListenerConfiguration UseStaticMembership(Func<string?>? instanceId = null)
    {
        add(topic =>
        {
            topic.ConsumerConfig ??= new ConsumerConfig();
            topic.ConsumerConfig.GroupInstanceId = KafkaStaticMembership.Resolve(instanceId);
            topic.StaticMembershipRequested = true;
        });
        return this;
    }

    /// <summary>
    /// Enable Kafka static group membership with an explicit <c>group.instance.id</c>. Discouraged unless
    /// the value is guaranteed unique per node. See GH-3139.
    /// </summary>
    public KafkaListenerConfiguration UseStaticMembership(string instanceId)
    {
        return UseStaticMembership(() => instanceId);
    }

    /// <summary>
    /// On a cold start (no committed offset for the group), begin reading from the *earliest* available
    /// offset (<c>auto.offset.reset = earliest</c>). Once the group has committed, it resumes from the
    /// committed position and this is ignored. See GH-3146.
    /// </summary>
    public KafkaListenerConfiguration BeginAtEarliest()
    {
        add(topic =>
        {
            topic.ConsumerConfig ??= new ConsumerConfig();
            topic.ConsumerConfig.AutoOffsetReset = AutoOffsetReset.Earliest;
        });
        return this;
    }

    /// <summary>
    /// On a cold start (no committed offset for the group), begin reading from the *latest* offset (the
    /// tail). Only applies when the group has no committed offset for a partition. See GH-3146.
    /// </summary>
    public KafkaListenerConfiguration BeginAtLatest()
    {
        add(topic =>
        {
            topic.ConsumerConfig ??= new ConsumerConfig();
            topic.ConsumerConfig.AutoOffsetReset = AutoOffsetReset.Latest;
        });
        return this;
    }

    /// <summary>
    /// Ephemeral "hot-tail" / broadcast consume (GH-3146): this listener joins a *unique per-process*
    /// consumer group and starts at the tail (<c>AutoOffsetReset.Latest</c>), so every node receives all
    /// messages and never replays — the idiomatic Kafka pattern for live dashboards and fan-out-to-all.
    /// No offsets are committed. Note: each process creates a transient consumer-group entry on the broker
    /// (Kafka expires these via <c>offsets.retention.minutes</c>).
    /// </summary>
    public KafkaListenerConfiguration TailFromLatest()
    {
        add(topic =>
        {
            topic.IsHotTail = true;
            topic.ConsumerConfig ??= new ConsumerConfig();
            topic.ConsumerConfig.AutoOffsetReset = AutoOffsetReset.Latest;
        });
        return this;
    }

    /// <summary>
    /// Opt into intra-partition concurrency by key (GH-3140): within each Kafka partition assigned to
    /// this node, messages with <em>different</em> keys are processed concurrently across
    /// <paramref name="numberOfSlots"/> slots while messages sharing the <em>same</em> key stay strictly
    /// ordered. This is the *second* concurrency lever — prefer scaling out (more partitions + nodes,
    /// see <see cref="UseCooperativeStickyAssignment"/>) first; reach for this for hot partitions or a
    /// capped partition count.
    ///
    /// Runs in durable mode: the Kafka offset is committed as each message is persisted to the inbox (in
    /// consumption order), and the inbox processing is sharded by key — so offset commit is decoupled
    /// from out-of-order completion and a crash/rebalance can't lose in-flight work. The grouping key is
    /// the Kafka message key by default; supply a custom grouping via
    /// <c>opts.Policies.PartitionMessagesByGroupId(...)</c> / message partitioning rules instead.
    /// </summary>
    public KafkaListenerConfiguration ProcessConcurrentlyByKey(PartitionSlots numberOfSlots)
    {
        add(topic =>
        {
            topic.GroupShardingSlotNumber = numberOfSlots;
            topic.GroupByMessageKey = true;
            // The grouping key must be the Kafka *message* key, not the consumer group, or every message
            // would hash to the same slot.
            topic.StampConsumerGroupIdOnEnvelope = false;
        });

        // The inbox is the reliability boundary for by-key concurrency (offset commit decoupled from
        // processing-completion order). See GH-3140.
        UseDurableInbox();
        return this;
    }

    /// <summary>
    /// Set this listener's consumer isolation level to <c>read_committed</c>, so records from aborted
    /// Kafka transactions are skipped when reading transactionally-written topics. Default is
    /// <c>read_uncommitted</c>. See GH-3149.
    /// </summary>
    public KafkaListenerConfiguration UseReadCommitted()
    {
        add(topic =>
        {
            topic.ConsumerConfig ??= new ConsumerConfig();
            topic.ConsumerConfig.IsolationLevel = IsolationLevel.ReadCommitted;
        });
        return this;
    }

    /// <summary>
    /// Configures circuit breaker behavior for this Kafka listener.
    /// </summary>
    /// <param name="configure">
    /// Optional configuration action for <see cref="CircuitBreakerOptions"/>.
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public KafkaListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        add(topic =>
        {
            topic.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure(topic.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    /// If you need to do anything "special" to create topics at runtime with Wolverine,
    /// this overrides the simple logic that Wolverine uses and replaces
    /// it with whatever you need to do having full access to the Kafka IAdminClient
    /// and the Wolverine KafkaTopic configuration
    /// </summary>
    /// <param name="creation"></param>
    /// <returns></returns>
    public KafkaListenerConfiguration TopicCreation(Func<IAdminClient, KafkaTopic, Task> creation)
    {
        if (creation == null)
        {
            throw new ArgumentNullException(nameof(creation));
        }

        add(topic => topic.CreateTopicFunc = creation);
        return this;
    }
    
    /// <summary>
    /// Configure this endpoint to receive messages of type T from
    /// JSON message bodies. This option maybe be necessary to receive
    /// messages from non-Wolverine applications
    /// </summary>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public KafkaListenerConfiguration ReceiveRawJson<T>(JsonSerializerOptions? options = null)
    {
        return ReceiveRawJson(typeof(T));
    }

    /// <summary>
    /// Configure this endpoint to receive messages of the message typ from
    /// JSON message bodies. This option maybe be necessary to receive
    /// messages from non-Wolverine applications
    /// </summary>
    /// <param name="messageType"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public KafkaListenerConfiguration ReceiveRawJson(Type messageType, JsonSerializerOptions? options = null)
    {
        DefaultIncomingMessage(messageType);
        return UseInterop((e, _) => new JsonOnlyMapper(e, options ?? new()));
    }
    
    /// <summary>
    /// Enable native dead letter queue support for this Kafka listener.
    /// Failed messages will be produced to the DLQ Kafka topic
    /// (default: "wolverine-dead-letter-queue") with exception details
    /// in Kafka headers.
    /// </summary>
    /// <returns></returns>
    public KafkaListenerConfiguration EnableNativeDeadLetterQueue()
    {
        add(topic => topic.NativeDeadLetterQueueEnabled = true);
        return this;
    }

    /// <summary>
    /// Disable native dead letter queue support for this Kafka listener.
    /// Failed messages will use Wolverine's default dead letter handling
    /// (database persistence).
    /// </summary>
    /// <returns></returns>
    public KafkaListenerConfiguration DisableNativeDeadLetterQueue()
    {
        add(topic => topic.NativeDeadLetterQueueEnabled = false);
        return this;
    }

    /// <summary>
    /// Disables stamping of the Kafka consumer group ID onto the GroupId property
    /// of each incoming envelope. Use this when the consumer group name is not
    /// meaningful as envelope metadata (e.g. when using PropagateGroupIdToPartitionKey).
    /// </summary>
    /// <returns></returns>
    public KafkaListenerConfiguration DisableConsumerGroupIdStamping()
    {
        add(topic => topic.StampConsumerGroupIdOnEnvelope = false);
        return this;
    }

    /// <summary>
    /// Marks this topic as owned by an external system. Wolverine
    /// will not attempt to create it during startup or delete it during resource
    /// teardown, even when AutoProvision is enabled on the parent transport.
    /// Use this when the calling identity lacks CreateTopics or DeleteTopics
    /// ACLs on the target topic.
    /// </summary>
    /// <returns></returns>
    public KafkaListenerConfiguration ExternallyOwned()
    {
        add(topic => topic.IsExternallyOwned = true);
        return this;
    }

    /// <summary>
    /// Configure the consumer config for only this topic. This overrides the default
    /// settings at the transport level. This is not combinatorial with the parent configuration
    /// and overwrites all ConsumerConfig from the parent
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public KafkaListenerConfiguration ConfigureConsumer(Action<ConsumerConfig> configuration)
    {
        add(topic =>
        {
            var config = new ConsumerConfig();
            configuration(config);

            topic.ConsumerConfig = config;
        });
        return this;
    }
    /// <summary>
    /// Extends the Kafka consumer settings for this topic without replacing the
    /// existing topic-level configuration. If no topic-specific configuration exists,
    /// a new <see cref="ConsumerConfig"/> is created before applying the changes.
    /// </summary>
    /// <param name="configuration">An action that adds or updates consumer settings for this topic.</param>
    /// <returns>The current <see cref="KafkaListenerConfiguration"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public KafkaListenerConfiguration ExtendConsumerConfiguration(Action<ConsumerConfig> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        add(topic =>
        {
            var values = topic.Parent.ConsumerConfig.
                ToDictionary(x => x.Key, x => x.Value);

            if (topic.ConsumerConfig != null)
            {
                foreach (var pair in topic.ConsumerConfig)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            var config = new ConsumerConfig(values);
            configuration(config);

            topic.ConsumerConfig = config;
        });

        return this;
    }
}
