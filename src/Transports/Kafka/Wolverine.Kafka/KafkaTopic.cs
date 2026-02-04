using Confluent.Kafka;
using Confluent.Kafka.Admin;
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

    public KafkaTransport Parent { get; }

    public KafkaTopic(KafkaTransport parent, string topicName, EndpointRole role) : base(new Uri($"{parent.Protocol}://topic/" + topicName), role)
    {
        Parent = parent;
        EndpointName = topicName;
        TopicName = topicName;

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

    public TopicSpecification Specification { get; } = new();

    public string TopicName { get; }

    /// <summary>
    /// Override for this specific Kafka Topic
    /// </summary>
    public ConsumerConfig? ConsumerConfig { get; internal set; }

    /// <summary>
    /// Override for this specific Kafka Topic
    /// </summary>
    public ProducerConfig? ProducerConfig { get; internal set; }

    /// <summary>
    /// When true, enables "at least once" delivery semantics by storing offsets
    /// only after message processing completes. This automatically sets
    /// EnableAutoOffsetStore=false on the consumer. Default is false for backward compatibility.
    /// </summary>
    public bool EnableAtLeastOnceDelivery { get; internal set; }

    public static string TopicNameForUri(Uri uri)
    {
        return uri.Segments.Last().Trim('/');
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        EnvelopeMapper ??= BuildMapper(runtime);

        var config = ConsumerConfig ?? Parent.ConsumerConfig;

        if (EnableAtLeastOnceDelivery)
        {
            config = new ConsumerConfig(config) { EnableAutoOffsetStore = false };
        }

        var listener = new KafkaListener(this, config,
            Parent.CreateConsumer(config), receiver, runtime.LoggerFactory.CreateLogger<KafkaListener>());
        return ValueTask.FromResult((IListener)listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        EnvelopeMapper ??= BuildMapper(runtime);

        return Mode == EndpointMode.Inline
            ? new InlineKafkaSender(this)
            : new BatchedSender(this, new KafkaSenderProtocol(this), runtime.Cancellation,
                runtime.LoggerFactory.CreateLogger<KafkaSenderProtocol>());
    }

    public async ValueTask<bool> CheckAsync()
    {
        // Can't do anything about this
        if (Parent.Usage == KafkaUsage.ConsumeOnly) return true;

        if (TopicName == WolverineTopicsName) return true; // don't care, this is just a marker
        try
        {
            using var client = Parent.CreateProducer(ProducerConfig);
            await client.ProduceAsync(TopicName, new Message<string, byte[]>
            {
                Key = "ping",
                Value = Encoding.Default.GetBytes("ping")
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
        using var adminClient = Parent.CreateAdminClient();
        await adminClient.DeleteTopicsAsync([TopicName]);
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        if (TopicName == WolverineTopicsName) return; // don't care, this is just a marker

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
    /// Override how this Kafka topic is created
    /// </summary>
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