using Confluent.Kafka;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

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

public class KafkaTransport : BrokerTransport<KafkaTopic>
{
    [IgnoreDescription]
    public Cache<string, KafkaTopic> Topics { get; }

    internal List<KafkaTopicGroup> TopicGroups { get; } = new();

    [ChildDescription]
    public ProducerConfig ProducerConfig { get; } = new();
    [IgnoreDescription]
    public Action<ProducerBuilder<string, byte[]>> ConfigureProducerBuilders { get; internal set; } = _ => {};

    [ChildDescription]
    public ConsumerConfig ConsumerConfig { get; } = new();
    [IgnoreDescription]
    public Action<ConsumerBuilder<string, byte[]>> ConfigureConsumerBuilders { get; internal set; } = _ => {};

    [ChildDescription]
    public AdminClientConfig AdminClientConfig { get; } = new();
    [IgnoreDescription]
    public Action<AdminClientBuilder> ConfigureAdminClientBuilders { get; internal set; } = _ => {};

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

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
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

        return ValueTask.CompletedTask;
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

    internal IProducer<string, byte[]> CreateProducer(ProducerConfig? config)
    {
        var producerBuilder = new ProducerBuilder<string, byte[]>(config ?? ProducerConfig);
        ConfigureProducerBuilders(producerBuilder);
        return producerBuilder.Build();
    }

    internal IConsumer<string, byte[]> CreateConsumer(ConsumerConfig? config)
    {
        var consumerBuilder = new ConsumerBuilder<string, byte[]>(config ?? ConsumerConfig);
        ConfigureConsumerBuilders(consumerBuilder);
        return consumerBuilder.Build();
    }

    internal IAdminClient CreateAdminClient()
    {
        var adminClientBuilder = new AdminClientBuilder(AdminClientConfig);
        ConfigureAdminClientBuilders(adminClientBuilder);
        return adminClientBuilder.Build();
    }
}
