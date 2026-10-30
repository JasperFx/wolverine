using System.Collections.Concurrent;
using Dekaf;
using Dekaf.Admin;
using Dekaf.Consumer;
using Dekaf.Producer;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using DekafClient = Dekaf.Kafka;

namespace Wolverine.Kafka.Internals;

internal class KafkaNamedConnectionSource
{
    public string BootstrapServers { get; }
    public KafkaNamedConnectionSource(string bootstrapServers) => BootstrapServers = bootstrapServers;
}

public enum KafkaUsage
{
    ProduceAndConsume,
    ProduceOnly,
    ConsumeOnly
}

public class KafkaTransport : BrokerTransport<KafkaTopic>, IAsyncDisposable
{
    // Dead letter senders are handed to receivers that never dispose them, so the transport
    // owns their lifetime and drains them when the runtime disposes its transports.
    private readonly object _ownedSenderLock = new();
    private readonly List<IAsyncDisposable> _ownedSenders = new();

    // Dead letter and retry-tier moves produce one record at a time, so they share a producer per topic and
    // producing cluster instead of connecting one per record
    private readonly ConcurrentDictionary<(KafkaTransport Producing, string Topic), LazyKafkaProducer>
        _recordProducers = new();

    [IgnoreDescription]
    public Cache<string, KafkaTopic> Topics { get; }

    internal List<KafkaTopicGroup> TopicGroups { get; } = new();

    /// <summary>
    /// Broker-per-tenant registrations (GH-3303). Each tenant owns a child transport pointed at its own Kafka
    /// cluster; outbound is routed by <see cref="Envelope.TenantId"/> and inbound listeners stamp the tenant id.
    /// </summary>
    [IgnoreDescription]
    internal LightweightCache<string, KafkaTenant> Tenants { get; } = new();

    // GH-3269: the client options carry SaslUsername/SaslPassword, so reflecting them into the diagnostic
    // tree would leak those secrets to monitoring consumers. The sanitized bootstrap-servers target is
    // surfaced via DescribeEndpoint() instead.
    [IgnoreDescription]
    public KafkaProducerOptions ProducerConfig { get; internal set; } = new();
    [IgnoreDescription]
    public Action<ProducerBuilder<string, byte[]>> ConfigureProducerBuilders { get; internal set; } = _ => {};

    [IgnoreDescription]
    public KafkaConsumerOptions ConsumerConfig { get; internal set; } = new();
    [IgnoreDescription]
    public Action<ConsumerBuilder<string, byte[]>> ConfigureConsumerBuilders { get; internal set; } = _ => {};

    [IgnoreDescription]
    public KafkaAdminClientOptions AdminClientConfig { get; internal set; } = new();
    [IgnoreDescription]
    public Action<AdminClientBuilder> ConfigureAdminClientBuilders { get; internal set; } = _ => {};

    public override string? DescribeEndpoint()
    {
        // BootstrapServers is a CSV of host:port (e.g. "broker1:9092, broker2:9092"); it carries no credentials.
        var bootstrap = ProducerConfig.BootstrapServers
                        ?? ConsumerConfig.BootstrapServers
                        ?? AdminClientConfig.BootstrapServers;
        return string.IsNullOrWhiteSpace(bootstrap) ? null : bootstrap;
    }

    public KafkaTransport() : this("kafka")
    {

    }

    public KafkaTransport(string protocol) : base(protocol, "Kafka Topics", ["kafka"])
    {
        Topics = new Cache<string, KafkaTopic>(topicName => new KafkaTopic(this, topicName, EndpointRole.Application));
    }

    /// <summary>
    /// The Kafka topic name used for native dead letter queue messages.
    /// Default is "wolverine-dead-letter-queue".
    /// </summary>
    public string DeadLetterQueueTopicName { get; set; } = DeadLetterQueueConstants.DefaultQueueName;

    public KafkaUsage Usage { get; set; } = KafkaUsage.ProduceAndConsume;

    /// <summary>
    /// True when static group membership was requested at the transport level (GH-3139). Used to emit a
    /// startup diagnostic about the resolved group.instance.id.
    /// </summary>
    internal bool StaticMembershipRequested { get; set; }

    public override Uri ResourceUri
    {
        get
        {
            var uri = new Uri($"{Protocol}://");

            var bootstrap = ConsumerConfig.BootstrapServers ??
                            ProducerConfig.BootstrapServers ?? AdminClientConfig.BootstrapServers;
            if (bootstrap.IsNotEmpty())
            {
                uri = new Uri(uri, bootstrap);
            }

            return uri;
        }
    }

    protected override IEnumerable<KafkaTopic> endpoints()
    {
        foreach (var topic in Topics) yield return topic;
        foreach (var group in TopicGroups) yield return group;
    }

    protected override KafkaTopic findEndpointByUri(Uri uri)
    {
        var topicName = KafkaTopic.TopicNameForUri(uri);
        return Topics[topicName];
    }

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
        ConsumerConfig.GroupId ??= runtime.Options.ServiceName;
        ProducerConfig.ClientId ??= runtime.Options.ServiceName;

        var topics = Topics[KafkaTopic.WolverineTopicsName];
        topics.RoutingType = RoutingMode.ByTopic;
        topics.OutgoingRules.Add(
            new TopicRoutingRule()); // t
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        // GH-4522: UseKafka() turns off automatic failure acks and said so nowhere -- not in a log line, not
        // in the docs. It is one of the answers to "why is this message being redelivered", so state it once
        // at startup where an operator reading the logs will find it.
        if (!runtime.Options.EnableAutomaticFailureAcks)
        {
            runtime.LoggerFactory.CreateLogger<KafkaTransport>().LogInformation(
                "Automatic failure acknowledgements are disabled for this application because the Kafka transport is in use. Kafka serialization failures cannot be acknowledged automatically, so a message that fails to deserialize is retried rather than acked and skipped.");
        }

        var namedConnection = runtime.Services.GetService<KafkaNamedConnectionSource>();
        if (namedConnection != null)
        {
            ConsumerConfig.BootstrapServers = namedConnection.BootstrapServers;
            ProducerConfig.BootstrapServers = namedConnection.BootstrapServers;
            AdminClientConfig.BootstrapServers = namedConnection.BootstrapServers;
        }

        var needsDlqTopic = false;
        foreach (var endpoint in Topics)
        {
            endpoint.Compile(runtime);
            if (endpoint.NativeDeadLetterQueueEnabled) needsDlqTopic = true;
        }

        foreach (var group in TopicGroups)
        {
            group.Compile(runtime);
            if (group.NativeDeadLetterQueueEnabled) needsDlqTopic = true;
        }

        // Ensure the DLQ topic is registered and compiled so it gets auto-provisioned
        if (needsDlqTopic)
        {
            var dlqTopic = Topics[DeadLetterQueueTopicName];
            dlqTopic.Compile(runtime);
        }

        warnOnStaticMembership(runtime);
        registerRetryTopicListeners(runtime);

        // Broker-per-tenant (GH-3303): clone the (now fully resolved) parent config onto each tenant's child
        // transport, re-pointing it at the tenant's own cluster. Producers/consumers/admin clients are lazy,
        // so there is no live connection to open here — but when AutoProvision is on we must create the shared
        // topic topology (including the DLQ topic) on every tenant cluster, since each is a separate broker.
        if (Tenants.Any())
        {
            foreach (var tenant in Tenants)
            {
                tenant.Compile(this, runtime.Options);
            }

            if (AutoProvision)
            {
                await provisionTenantTopicsAsync(runtime);
            }
        }
    }

    // Create the shared application topics (and the DLQ topic) on each tenant's own cluster. Mirrors the specs
    // the parent would provision via KafkaTopic.SetupAsync, but runs against the tenant admin client.
    private async Task provisionTenantTopicsAsync(IWolverineRuntime runtime)
    {
        var logger = runtime.LoggerFactory.CreateLogger<KafkaTransport>();

        var singleTopics = Topics
            .Where(t => t.TopicName != KafkaTopic.WolverineTopicsName && !t.IsExternallyOwned)
            .ToArray();

        foreach (var tenant in Tenants)
        {
            await using var adminClient = tenant.Transport.CreateAdminClient();

            foreach (var topic in singleTopics)
            {
                await topic.SetupOnAsync(adminClient, logger);
            }

            foreach (var group in TopicGroups.Where(g => !g.IsExternallyOwned))
            {
                await group.SetupOnAsync(adminClient, logger);
            }
        }
    }

    // GH-3148: discover the non-blocking retry-topic policy (MoveToKafkaRetryTopic) in the global failure
    // rules and auto-register a delayed listener per source topic × tier, plus a startup validation warning
    // if the policy could apply to non-Kafka endpoints (where it degrades to an inline retry).
    private void registerRetryTopicListeners(IWolverineRuntime runtime)
    {
        var delays = new HashSet<TimeSpan>();
        foreach (var rule in runtime.Options.HandlerGraph.Failures)
        {
            if (rule.InfiniteSource is MoveToKafkaRetryTopicContinuation continuation)
            {
                foreach (var delay in continuation.Delays)
                {
                    delays.Add(delay);
                }
            }
        }

        if (delays.Count == 0)
        {
            return;
        }

        var logger = runtime.LoggerFactory.CreateLogger<KafkaTransport>();

        // Validation: this policy only routes Kafka messages (the continuation self-guards), so warn if
        // any non-Kafka listener exists where it will silently degrade to an inline retry.
        var hasNonKafkaListener = runtime.Options.Transports
            .Where(t => t is not KafkaTransport)
            .SelectMany(t => t.Endpoints())
            .Any(e => e.IsListener);
        if (hasNonKafkaListener)
        {
            logger.LogWarning(
                "MoveToKafkaRetryTopic is configured but non-Kafka listeners are present. The Kafka retry topics only apply to messages received over Kafka; failures on other transports will fall back to an inline retry.");
        }

        // Source application listener topics (exclude the DLQ, system, and existing retry-tier topics).
        var sources = Topics
            .Where(t => t.IsListener && t.RetryTierDelay == null && t.TopicName != DeadLetterQueueTopicName
                        && t.TopicName != KafkaTopic.WolverineTopicsName && !t.TopicName.Contains(".retry."))
            .Select(t => t.TopicName)
            .ToList();

        foreach (var source in sources)
        {
            foreach (var delay in delays)
            {
                var tierTopic = Topics[KafkaRetryNaming.RetryTopicName(source, delay)];
                tierTopic.IsListener = true;
                tierTopic.RetryTierDelay = delay;
                tierTopic.Mode = EndpointMode.Inline;

                // The LAST tier's exhaustion path is MoveToDeadLetterQueueAsync, and that decision is made
                // against the TIER listener's endpoint — not the source topic the message originally
                // arrived on. Without inheriting the source's native-DLQ enablement, an exhausted message
                // never reached the Kafka dead letter topic at all: on a durable host it fell back to the
                // database DLQ, and on a storeless host it was simply dropped. The test covering this
                // passed vacuously for months by consuming another suite's residue off the shared default
                // DLQ topic.
                tierTopic.NativeDeadLetterQueueEnabled = Topics[source].NativeDeadLetterQueueEnabled;
                // Stable group + earliest so a tier consumer never misses a just-produced retry record
                // (a Latest consumer races the producer). Reads + commits its own retry-topic offsets.
                tierTopic.ConsumerConfig = new KafkaConsumerOptions
                {
                    GroupId = $"{runtime.Options.ServiceName}-retry",
                    AutoOffsetReset = AutoOffsetReset.Earliest
                };
                tierTopic.Compile(runtime);

                logger.LogInformation("Registered Kafka retry-tier listener {Topic} (delay {Delay})",
                    tierTopic.TopicName, delay);
            }
        }
    }

    // GH-3139: surface the resolved group.instance.id so operators can verify per-node uniqueness, and
    // warn loudly if static membership was requested but no stable id could be resolved.
    private void warnOnStaticMembership(IWolverineRuntime runtime)
    {
        var logger = runtime.LoggerFactory.CreateLogger<KafkaTransport>();

        void Check(bool requested, string? instanceId, string scope)
        {
            if (!requested) return;

            if (string.IsNullOrWhiteSpace(instanceId))
            {
                logger.LogWarning(
                    "Kafka static membership was requested ({Scope}) but no stable group.instance.id could be resolved (checked the supplied source, POD_NAME, HOSTNAME, and machine name). Static membership will not take effect and rolling restarts may trigger partition rebalancing. Provide an explicit per-node id via UseStaticMembership(...).",
                    scope);
            }
            else
            {
                logger.LogInformation(
                    "Kafka static membership enabled ({Scope}) with group.instance.id '{InstanceId}'. Ensure this value is unique per node and stable across restarts of the same node.",
                    scope, instanceId);
            }
        }

        Check(StaticMembershipRequested, ConsumerConfig.GroupInstanceId, "transport");

        foreach (var topic in Topics.Where(x => x.StaticMembershipRequested))
        {
            Check(true, topic.GetEffectiveConsumerConfig().GroupInstanceId, $"topic '{topic.TopicName}'");
        }
    }

    public WolverineTransportHealthCheck BuildHealthCheck(IWolverineRuntime runtime)
    {
        return new KafkaHealthCheck(this);
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield break;
    }

    internal async ValueTask<IKafkaProducer<string, byte[]>> CreateProducerAsync(KafkaProducerOptions? config)
    {
        var producerBuilder = DekafClient.CreateProducer<string, byte[]>()
            .ApplyOptions(config ?? ProducerConfig);
        ConfigureProducerBuilders(producerBuilder);
        return await producerBuilder.BuildAsync(CancellationToken.None);
    }

    /// <summary>
    /// Builds the consumer WITHOUT connecting. Listeners initialize it from their receive loop with
    /// retries so a host still starts (and keeps retrying) when the broker is unreachable; one-shot
    /// consumers (replay) call InitializeAsync themselves.
    /// </summary>
    internal IKafkaConsumer<string, byte[]> CreateConsumer(KafkaConsumerOptions? config)
    {
        var consumerBuilder = DekafClient.CreateConsumer<string, byte[]>()
            .ApplyOptions(config ?? ConsumerConfig);
        ConfigureConsumerBuilders(consumerBuilder);
        return consumerBuilder.Build();
    }

    internal IAdminClient CreateAdminClient()
    {
        var adminClientBuilder = DekafClient.CreateAdminClient()
            .ApplyOptions(AdminClientConfig);
        ConfigureAdminClientBuilders(adminClientBuilder);
        return adminClientBuilder.Build();
    }

    internal InlineKafkaSender TrackOwnedSender(InlineKafkaSender sender)
    {
        lock (_ownedSenderLock)
        {
            _ownedSenders.Add(sender);
        }

        return sender;
    }

    /// <summary>
    /// The shared producer for one-off produces on behalf of <paramref name="topic" /> to the cluster of
    /// <paramref name="producingTransport" />, which is this transport or one of its tenants' (GH-3303). Owned by
    /// this transport, because tenant transports are never disposed.
    /// </summary>
    internal LazyKafkaProducer RecordProducerFor(KafkaTopic topic, KafkaTransport producingTransport)
    {
        return _recordProducers.GetOrAdd((producingTransport, topic.TopicName),
            static (key, topic) => LazyKafkaProducer.For(topic, key.Producing), topic);
    }

    public async ValueTask DisposeAsync()
    {
        List<IAsyncDisposable> owned;
        lock (_ownedSenderLock)
        {
            owned = [.._ownedSenders];
            _ownedSenders.Clear();
        }

        owned.AddRange(_recordProducers.Values);
        _recordProducers.Clear();

        // Each drain is bounded, and they are independent
        await Task.WhenAll(owned.Select(x => x.DisposeAsync().AsTask()));
    }
}
