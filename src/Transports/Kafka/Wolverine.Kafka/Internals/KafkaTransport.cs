using Confluent.Kafka;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

public class KafkaTransport : BrokerTransport<KafkaTopic>
{
    public Cache<string, KafkaTopic> Topics { get; }

    public ProducerConfig ProducerConfig { get; } = new();
    public Action<ProducerBuilder<string, byte[]>> ConfigureProducerBuilders { get; internal set; } = _ => {};

    public ConsumerConfig ConsumerConfig { get; } = new();
    public Action<ConsumerBuilder<string, byte[]>> ConfigureConsumerBuilders { get; internal set; } = _ => {};

    public AdminClientConfig AdminClientConfig { get; } = new();
    public Action<AdminClientBuilder> ConfigureAdminClientBuilders { get; internal set; } = _ => {};

    public KafkaTransport() : this("kafka")
    {
        
    }

    public KafkaTransport(string protocol) : base(protocol, "Kafka Topics")
    {
        Topics = new Cache<string, KafkaTopic>(topicName => new KafkaTopic(this, topicName, EndpointRole.Application));
    }

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
        return Topics;
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
        foreach (var endpoint in Topics) endpoint.Compile(runtime);

        return ValueTask.CompletedTask;
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
