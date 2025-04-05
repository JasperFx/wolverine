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
    public Action<ProducerBuilder<string, string>> ConfigureProducerBuilders { get; internal set; } = _ => {};

    public ConsumerConfig ConsumerConfig { get; } = new();

    public AdminClientConfig AdminClientConfig { get; } = new();

    public KafkaTransport() : base("kafka", "Kafka Topics")
    {
        Topics = new Cache<string, KafkaTopic>(topicName => new KafkaTopic(this, topicName, EndpointRole.Application));
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

    internal IProducer<string, string> CreateProducer()
    {
        var producerBuilder = new ProducerBuilder<string, string>(ProducerConfig);
        ConfigureProducerBuilders(producerBuilder);
        return producerBuilder.Build();
    }
}
