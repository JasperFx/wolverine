using Confluent.Kafka;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;

namespace Wolverine.Kafka;

public class KafkaTransportExpression : BrokerExpression<KafkaTransport, KafkaTopic, KafkaTopic, KafkaListenerConfiguration, KafkaSubscriberConfiguration, KafkaTransportExpression>
{
    private readonly KafkaTransport _transport;

    internal KafkaTransportExpression(KafkaTransport transport, WolverineOptions options) : base(transport, options)
    {
        _transport = transport;
    }

    /// <summary>
    /// Configure both the producer and consumer config of the underlying Kafka client
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureClient(Action<ClientConfig> configure)
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
    public KafkaTransportExpression ConfigureProducers(Action<ProducerConfig> configure)
    {
        configure(_transport.ProducerConfig);
        return this;
    }

    /// <summary>
    /// Configure the Kafka message producer builders within the Wolverine transport
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
    public KafkaTransportExpression ConfigureConsumers(Action<ConsumerConfig> configure)
    {
        configure(_transport.ConsumerConfig);
        return this;
    }

    /// <summary>
    /// Configure the Kafka consumer builders within the Wolverine transport
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureConsumerBuilders(Action<ConsumerBuilder<string, byte[]>> configure)
    {
        _transport.ConfigureConsumerBuilders = configure;
        return this;
    }

    /// <summary>
    /// Opt every Kafka consumer on this node into cooperative-sticky rebalancing
    /// (<c>partition.assignment.strategy = CooperativeSticky</c>) so a rebalance keeps each consumer's
    /// unaffected partitions instead of a stop-the-world revoke-everything rebalance. Opt-in: do not
    /// switch an existing group between eager and cooperative assignors during a live rolling upgrade.
    /// See GH-3139 and the Kafka "Scaling out" docs.
    /// </summary>
    public KafkaTransportExpression UseCooperativeStickyAssignment()
    {
        _transport.ConsumerConfig.PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky;
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
    /// Opt every Kafka producer on this node into the idempotent producer
    /// (<c>enable.idempotence = true</c>, which implies <c>acks=all</c> and bounded in-flight requests),
    /// so producer-side retries can't write duplicates to the broker. Opt-in; slight throughput cost.
    /// This is producer→broker de-duplication only — it is not transactional exactly-once. See GH-3149.
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
    public KafkaTransportExpression AutoProvision(Action<AdminClientConfig>? configure = null)
    {
        _transport.AutoProvision = true;
        configure?.Invoke(_transport.AdminClientConfig);
        return this;
    }

    /// <summary>
    /// Configure the Kafka admin client builders within the Wolverine transport
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
