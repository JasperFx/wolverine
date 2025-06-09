using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka;

public class KafkaTopic : Endpoint, IBrokerEndpoint
{
    // Strictly an identifier for the endpoint
    public const string WolverineTopicsName = "wolverine.topics";

    public KafkaTransport Parent { get; }

    public KafkaTopic(KafkaTransport parent, string topicName, EndpointRole role) : base(new Uri($"{parent.Protocol}://topic/" + topicName), role)
    {
        Parent = parent;
        EndpointName = topicName;
        TopicName = topicName;

        Mapper = new KafkaEnvelopeMapper(this);
    }

    public override bool AutoStartSendingAgent()
    {
        return true;
    }

    public string TopicName { get; }

    public IKafkaEnvelopeMapper Mapper { get; set; }
    
    /// <summary>
    /// Override for this specific Kafka Topic
    /// </summary>
    public ConsumerConfig? ConsumerConfig { get; internal set; }

    /// <summary>
    /// Override for this specific Kafka Topic
    /// </summary>
    public ProducerConfig? ProducerConfig { get; internal set; }

    public static string TopicNameForUri(Uri uri)
    {
        return uri.Segments.Last().Trim('/');
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var listener = new KafkaListener(this, ConsumerConfig ?? Parent.ConsumerConfig,
            Parent.CreateConsumer(ConsumerConfig), receiver, runtime.LoggerFactory.CreateLogger<KafkaListener>());
        return ValueTask.FromResult((IListener)listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return Mode == EndpointMode.Inline
            ? new InlineKafkaSender(this)
            : new BatchedSender(this, new KafkaSenderProtocol(this), runtime.Cancellation,
                runtime.LoggerFactory.CreateLogger<KafkaSenderProtocol>());
    }

    public async ValueTask<bool> CheckAsync()
    {
        if (TopicName == WolverineTopicsName) return true; // don't care, this is just a marker
        try
        {
            using var client = Parent.CreateProducer(ProducerConfig);
            await client.ProduceAsync(TopicName, new Message<string, string>
            {
                Key = "ping",
                Value = "ping"
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

        try
        {
            await adminClient.CreateTopicsAsync(
            [
                new TopicSpecification
                {
                    Name = TopicName
                }
            ]);

            logger.LogInformation("Created Kafka topic {Topic}", TopicName);
        }
        catch (CreateTopicsException e)
        {
            if (e.Message.Contains("already exists.")) return;
            throw;
        }
    }
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