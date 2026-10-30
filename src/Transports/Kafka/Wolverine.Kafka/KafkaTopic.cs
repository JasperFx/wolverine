using Dekaf.Admin;
using Dekaf.Errors;
using Dekaf.Protocol;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
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
    public KafkaTopicSpecification Specification { get; } = new();

    public string TopicName { get; }

    /// <summary>
    /// Override for this specific Kafka Topic
    /// </summary>
    [ChildDescription]
    public KafkaConsumerOptions? ConsumerConfig { get; internal set; }

    /// <summary>
    /// Override for this specific Kafka Topic
    /// </summary>
    [ChildDescription]
    public KafkaProducerOptions? ProducerConfig { get; internal set; }

    /// <summary>
    /// How this listener commits consumer offsets back to Kafka. Defaults to
    /// <see cref="Kafka.CommitMode.StoreThenAutoFlush"/> — the non-blocking, idiomatic high-throughput
    /// model. See GH-3150. Inherited by <see cref="KafkaTopicGroup"/>.
    /// </summary>
    public CommitMode CommitMode { get; set; } = CommitMode.StoreThenAutoFlush;

    /// <summary>
    /// For durable (inbox-backed) listeners, the maximum number of already-fetched records the
    /// consume loop will drain in one pass so the inbox can persist them with one batched
    /// insert instead of one insert per record. 1 reverts to strict record-at-a-time consumption.
    /// Ignored for Buffered/Inline endpoints and retry-tier topics. Default 100. See GH-3490.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 100;

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
    /// Gets the effective consumer options for this topic, inheriting the parent's connection settings and
    /// GroupId where the topic does not set them
    /// </summary>
    internal KafkaConsumerOptions GetEffectiveConsumerConfig()
    {
        if (ConsumerConfig != null)
        {
            ConsumerConfig.InheritClientDefaultsFrom(Parent.ConsumerConfig);
            ConsumerConfig.GroupId ??= Parent.ConsumerConfig.GroupId;
        }

        return ConsumerConfig ?? Parent.ConsumerConfig;
    }

    /// <summary>
    /// Gets the effective producer options for this topic, inheriting the parent's connection settings where
    /// the topic does not set them
    /// </summary>
    internal KafkaProducerOptions GetEffectiveProducerConfig()
    {
        ProducerConfig?.InheritClientDefaultsFrom(Parent.ProducerConfig);

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

    /// <summary>
    /// The Kafka topics this endpoint's listener subscribes to
    /// </summary>
    protected virtual string[] listenedTopicNames => [TopicName];

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        EnvelopeMapper ??= BuildMapper(runtime);

        var config = GetEffectiveConsumerConfig();

        ApplyHotTailConfig(config, runtime);

        // Wire the Kafka client for the configured commit strategy (GH-3150). The default
        // StoreThenAutoFlush mode relies on the client's background committer flushing manually
        // stored offsets.
        KafkaOffsetCommitter.ApplyTo(config, CommitMode);

        var topicNames = listenedTopicNames;
        var listener = new KafkaListener(this, config,
            Parent.CreateConsumer(config), topicNames, receiver,
            runtime.LoggerFactory.CreateLogger<KafkaListener>(),
            runtime.DurabilitySettings.DrainTimeout);

        // Broker-per-tenant (GH-3303): the shared listener consumes the default cluster. Each tenant runs its
        // own listener on its own cluster, stamping the tenant id onto inbound envelopes via TenantIdRule.
        // Per-envelope completion routes back over the receiving connection through Envelope.Listener — mirrors
        // the RabbitMQ / NATS CompoundListener multi-tenancy pattern.
        if (Parent.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var compound = new CompoundListener(Uri);
            compound.Inner.Add(listener);

            foreach (var tenant in Parent.Tenants)
            {
                var tenantConfig = cloneConsumerConfigForTenant(config, tenant);
                var tenantReceiver = new ReceiverWithRules(receiver, [new TenantIdRule(tenant.TenantId)]);
                var tenantListener = new KafkaListener(this, tenantConfig,
                    tenant.Transport.CreateConsumer(tenantConfig), topicNames, tenantReceiver,
                    runtime.LoggerFactory.CreateLogger<KafkaListener>(), runtime.DurabilitySettings.DrainTimeout,
                    tenant.Transport);
                compound.Inner.Add(tenantListener);
            }

            return ValueTask.FromResult<IListener>(compound);
        }

        return ValueTask.FromResult<IListener>(listener);
    }

    /// <summary>
    /// Clone an already-resolved effective consumer config (commit strategy + hot-tail already applied) and
    /// re-point it at the tenant's cluster. The GroupId is deliberately preserved unchanged — each tenant is a
    /// separate cluster, so offsets are isolated and there is no reason to suffix the group id (GH-3303).
    /// </summary>
    internal static KafkaConsumerOptions cloneConsumerConfigForTenant(KafkaConsumerOptions source, KafkaTenant tenant)
    {
        var clone = source.Clone();
        clone.BootstrapServers = tenant.Transport.ConsumerConfig.BootstrapServers;

        return clone;
    }

    /// <summary>
    /// For an ephemeral hot-tail listener (GH-3146), assign a unique per-process consumer group so every
    /// node receives all messages and never replays, and disable Wolverine-managed commits (the
    /// position is throwaway). Setting EnableAutoCommit=true makes the commit strategy hands-off.
    /// </summary>
    private protected void ApplyHotTailConfig(KafkaConsumerOptions config, IWolverineRuntime runtime)
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

        // Broker-per-tenant (GH-3303): route by Envelope.TenantId to a per-tenant sender bound to that tenant's
        // own cluster, falling back to the shared cluster for the default/untenanted path.
        //
        // Both the tenant senders AND the default sender they fall back to must be simple fire-and-forget
        // ISenders here: TenantedSender intentionally does NOT implement ISenderRequiresCallback (GH-2361), and
        // it does not forward RegisterCallback to the senders beneath it. A BatchedSender registered under it
        // would therefore never receive its ISenderCallback and would silently drop every message. InlineKafka-
        // Sender produces + flushes directly and needs no callback — the same fire-and-forget model the RabbitMQ
        // and NATS broker-per-tenant senders use.
        if (Parent.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var tenantedSender = new TenantedSender(Uri, Parent.TenantedIdBehavior, new InlineKafkaSender(this, Parent));
            foreach (var tenant in Parent.Tenants)
            {
                tenantedSender.RegisterSender(tenant.TenantId, new InlineKafkaSender(this, tenant.Transport));
            }

            return tenantedSender;
        }

        return SendsInline
            ? new InlineKafkaSender(this)
            : new BatchedSender(this,
                new KafkaSenderProtocol(this) { Logger = runtime.LoggerFactory.CreateLogger<KafkaSenderProtocol>() },
                runtime.Cancellation, runtime.LoggerFactory.CreateLogger<KafkaSenderProtocol>());
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        if (NativeDeadLetterQueueEnabled)
        {
            var dlqTopic = Parent.Topics[Parent.DeadLetterQueueTopicName];
            dlqTopic.EnvelopeMapper ??= dlqTopic.BuildMapper(runtime);
            deadLetterSender = Parent.TrackOwnedSender(new InlineKafkaSender(dlqTopic, fixedDestination: true));
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
            await using var client = await Parent.CreateProducerAsync(GetEffectiveProducerConfig());
            await client.ProduceAsync(TopicName, "ping", "ping"u8.ToArray(), CancellationToken.None);

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

        await using var adminClient = Parent.CreateAdminClient();
        await adminClient.DeleteTopicsAsync([TopicName], new DeleteTopicsOptions(), CancellationToken.None);
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        if (TopicName == WolverineTopicsName) return; // don't care, this is just a marker

        if (IsExternallyOwned) return;

        await using var adminClient = Parent.CreateAdminClient();
        await SetupOnAsync(adminClient, logger);
    }

    /// <summary>
    /// Create this topic on the supplied admin client. Split out from <see cref="SetupAsync"/> so the same
    /// spec/creation logic can be applied against a tenant cluster's admin client (broker-per-tenant, GH-3303).
    /// </summary>
    internal async ValueTask SetupOnAsync(IAdminClient adminClient, ILogger logger)
    {
        if (TopicName == WolverineTopicsName) return; // don't care, this is just a marker

        if (IsExternallyOwned) return;

        Specification.Name = TopicName;

        try
        {
            await CreateTopicFunc(adminClient, this);

            logger.LogInformation("Created Kafka topic {Topic}", TopicName);
        }
        catch (KafkaException e) when (e.ErrorCode == ErrorCode.TopicAlreadyExists)
        {
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
    public Func<IAdminClient, KafkaTopic, Task> CreateTopicFunc { get; internal set; } = (c, t) =>
        c.CreateTopicsAsync([t.Specification.ToNewTopic()], new CreateTopicsOptions(), CancellationToken.None).AsTask();
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
