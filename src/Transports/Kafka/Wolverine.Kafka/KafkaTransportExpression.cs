using Dekaf;
using Dekaf.Admin;
using Dekaf.Consumer;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using IsolationLevel = Dekaf.Protocol.Messages.IsolationLevel;

namespace Wolverine.Kafka;

public class KafkaTransportExpression : BrokerExpression<KafkaTransport, KafkaTopic, KafkaTopic, KafkaListenerConfiguration, KafkaSubscriberConfiguration, KafkaTransportExpression>
{
    private readonly KafkaTransport _transport;

    internal KafkaTransportExpression(KafkaTransport transport, WolverineOptions options) : base(transport, options)
    {
        _transport = transport;
    }

    /// <summary>
    /// Configure the connection settings shared by producers, consumers, and admin clients of the
    /// underlying Kafka client
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureClient(Action<KafkaClientOptions> configure)
    {
        configure(_transport.ConsumerConfig);
        configure(_transport.ProducerConfig);
        configure(_transport.AdminClientConfig);

        return this;
    }

    /// <summary>
    /// Configure the Kafka message producers within the Wolverine transport. This can be
    /// overridden at the topic level.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureProducers(Action<KafkaProducerOptions> configure)
    {
        configure(_transport.ProducerConfig);
        return this;
    }

    /// <summary>
    /// Configure the Kafka message producer builders within the Wolverine transport. Use this as the
    /// escape hatch to any Dekaf producer setting Wolverine's options don't cover.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureProducerBuilders(Action<ProducerBuilder<string, byte[]>> configure)
    {
        _transport.ConfigureProducerBuilders = configure;
        return this;
    }

    /// <summary>
    /// Configure the Kafka message consumers within the Wolverine transport. This can be
    /// overridden at the topic level.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureConsumers(Action<KafkaConsumerOptions> configure)
    {
        configure(_transport.ConsumerConfig);
        return this;
    }

    /// <summary>
    /// Configure the Kafka consumer builders within the Wolverine transport. Use this as the
    /// escape hatch to any Dekaf consumer setting Wolverine's options don't cover.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureConsumerBuilders(Action<ConsumerBuilder<string, byte[]>> configure)
    {
        _transport.ConfigureConsumerBuilders = configure;
        return this;
    }

    /// <summary>
    /// No-op retained for source compatibility. The Dekaf client speaks only the KIP-848 consumer
    /// protocol, where rebalances are broker-driven and incremental by design — cooperative-sticky
    /// client-side assignment no longer applies.
    /// </summary>
    [Obsolete("Rebalances are always incremental under the KIP-848 consumer protocol; this call has no effect and will be removed in a future version")]
    public KafkaTransportExpression UseCooperativeStickyAssignment()
    {
        return this;
    }

    /// <summary>
    /// No-op retained for source compatibility. The Dekaf client always uses the KIP-848
    /// next-generation consumer rebalance protocol (which requires a Kafka 4.0+ broker).
    /// </summary>
    [Obsolete("The KIP-848 consumer protocol is always on; this call has no effect and will be removed in a future version")]
    public KafkaTransportExpression UseNextGenerationRebalanceProtocol()
    {
        return this;
    }

    /// <summary>
    /// Enable Kafka static group membership (<c>group.instance.id</c>) so rolling restarts/deploys of the
    /// same node don't trigger partition churn. The id is resolved from <paramref name="instanceId"/> if
    /// supplied, otherwise from <c>POD_NAME</c>, then <c>HOSTNAME</c>, then the machine name. The id MUST
    /// be unique per node and stable across restarts of that node — Wolverine logs the resolved value at
    /// startup so you can verify. See GH-3139.
    /// </summary>
    public KafkaTransportExpression UseStaticMembership(Func<string?>? instanceId = null)
    {
        _transport.StaticMembershipRequested = true;
        _transport.ConsumerConfig.GroupInstanceId = KafkaStaticMembership.Resolve(instanceId);
        return this;
    }

    /// <summary>
    /// Enable Kafka static group membership with an explicit <c>group.instance.id</c>. Discouraged unless
    /// the caller guarantees the value is unique per node (a single literal applied to every node makes
    /// Kafka fence all but one out and silently lose messages). See GH-3139.
    /// </summary>
    public KafkaTransportExpression UseStaticMembership(string instanceId)
    {
        return UseStaticMembership(() => instanceId);
    }

    /// <summary>
    /// Default consumers on this node to begin from the *earliest* available offset on a cold start
    /// (<c>auto.offset.reset = earliest</c>). This only applies the first time a consumer group reads a
    /// partition — once the group has a committed offset, it resumes there and this is ignored. See GH-3146.
    /// </summary>
    public KafkaTransportExpression BeginAtEarliest()
    {
        _transport.ConsumerConfig.AutoOffsetReset = AutoOffsetReset.Earliest;
        return this;
    }

    /// <summary>
    /// Default consumers on this node to begin from the *latest* offset (the tail) on a cold start
    /// (<c>auto.offset.reset = latest</c>). Only applies when the group has no committed offset for a
    /// partition. See GH-3146.
    /// </summary>
    public KafkaTransportExpression BeginAtLatest()
    {
        _transport.ConsumerConfig.AutoOffsetReset = AutoOffsetReset.Latest;
        return this;
    }

    /// <summary>
    /// Pin every Kafka producer on this node to the idempotent producer
    /// (<c>enable.idempotence = true</c>, which implies <c>acks=all</c> and bounded in-flight requests),
    /// so producer-side retries can't write duplicates to the broker. Dekaf producers are idempotent by
    /// default, so this only matters when other configuration turns idempotence off.
    /// This is producer→broker de-duplication only, not transactional exactly-once. See GH-3149.
    /// </summary>
    public KafkaTransportExpression UseIdempotentProducer()
    {
        _transport.ProducerConfig.EnableIdempotence = true;
        return this;
    }

    /// <summary>
    /// Set the consumer isolation level for every consumer on this node to <c>read_committed</c>, so
    /// records from aborted Kafka transactions are skipped when reading transactionally-written topics.
    /// Default is <c>read_uncommitted</c>. See GH-3149.
    /// </summary>
    public KafkaTransportExpression UseReadCommitted()
    {
        _transport.ConsumerConfig.IsolationLevel = IsolationLevel.ReadCommitted;
        return this;
    }

    /// <summary>
    /// Create newly used Kafka topics on endpoint activation if the topic is missing
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression AutoProvision(Action<KafkaAdminClientOptions>? configure = null)
    {
        _transport.AutoProvision = true;
        configure?.Invoke(_transport.AdminClientConfig);
        return this;
    }

    /// <summary>
    /// Configure the Kafka admin client builders within the Wolverine transport. Use this as the
    /// escape hatch to any Dekaf admin client setting Wolverine's options don't cover.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureAdminClientBuilders(Action<AdminClientBuilder> configure)
    {
        _transport.ConfigureAdminClientBuilders = configure;
        return this;
    }

    /// <summary>
    /// Deletes and rebuilds topics on application startup
    /// </summary>
    /// <returns></returns>
    public KafkaTransportExpression DeleteExistingTopicsOnStartup()
    {
        _transport.AutoPurgeAllQueues = true;
        return this;
    }

    /// <summary>
    /// Configure the Kafka topic name used for native dead letter queue messages.
    /// Default is "wolverine-dead-letter-queue".
    /// </summary>
    /// <param name="topicName">The Kafka topic name for the dead letter queue</param>
    /// <returns></returns>
    public KafkaTransportExpression DeadLetterQueueTopicName(string topicName)
    {
        _transport.DeadLetterQueueTopicName = topicName;
        return this;
    }

    /// <summary>
    /// Override the sending behavior for unknown or missing tenant ids when using broker-per-tenant Kafka
    /// multi-tenancy (GH-3303). See <see cref="TenantedIdBehavior"/>. Default is
    /// <see cref="Wolverine.Transports.Sending.TenantedIdBehavior.FallbackToDefault"/> unless changed.
    /// </summary>
    public KafkaTransportExpression TenantIdBehavior(TenantedIdBehavior behavior)
    {
        _transport.TenantedIdBehavior = behavior;
        return this;
    }

    /// <summary>
    /// Register a tenant that is served by its own dedicated Kafka cluster identified by
    /// <paramref name="bootstrapServers"/>, while sharing the topic topology declared on this transport. The
    /// tenant inherits the parent's producer/consumer/admin configuration (auth, SASL/TLS, idempotence, static
    /// membership, DLQ topic name, …) with only the bootstrap servers re-pointed. Outbound messages carrying a
    /// matching <see cref="Envelope.TenantId"/> are routed to this cluster; inbound messages consumed from it
    /// are stamped with the tenant id.
    ///
    /// The consumer group id is intentionally inherited unchanged — each tenant is a separate cluster with its
    /// own offsets, so there is no need (and it would be incorrect) to suffix the group id per tenant.
    /// </summary>
    public KafkaTransportExpression AddTenant(string tenantId, string bootstrapServers)
    {
        _transport.Tenants[tenantId] = new KafkaTenant(tenantId) { BootstrapServers = bootstrapServers };
        return this;
    }

    /// <summary>
    /// Register a tenant served by its own dedicated Kafka cluster, configured through the full Kafka transport
    /// surface (auth, SASL/TLS, advanced client options). The <paramref name="configure"/> action runs against a
    /// configuration seeded from this transport's own settings, so you only override what differs for the tenant
    /// (typically <c>ConfigureClient(c =&gt; c.BootstrapServers = ...)</c> plus any tenant-specific credentials).
    /// </summary>
    public KafkaTransportExpression AddTenant(string tenantId, Action<KafkaTransportExpression> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _transport.Tenants[tenantId] = new KafkaTenant(tenantId) { Configure = configure };
        return this;
    }

    protected override KafkaListenerConfiguration createListenerExpression(KafkaTopic listenerEndpoint)
    {
        return new KafkaListenerConfiguration(listenerEndpoint);
    }

    protected override KafkaSubscriberConfiguration createSubscriberExpression(KafkaTopic subscriberEndpoint)
    {
        return new KafkaSubscriberConfiguration(subscriberEndpoint);
    }

    /// <summary>
    /// In normal usage Wolverine will try to create a producer for each topic it listens to just for
    /// "Requeue" actions. In the case of an application that only consumes messages from Kafka, use
    /// this setting to disable that behavior and eliminate any error messages from that behavior
    /// </summary>
    /// <returns></returns>
    public KafkaTransportExpression ConsumeOnly()
    {
        _transport.Usage = KafkaUsage.ConsumeOnly;
        return this;
    }
}
