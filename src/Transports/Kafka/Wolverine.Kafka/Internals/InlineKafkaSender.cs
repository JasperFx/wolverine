using Wolverine.Transports.Sending;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Produces each envelope as it is sent, over the shared producer of its cluster's <see cref="KafkaProducerPool" />
/// </summary>
public class InlineKafkaSender : ISender
{
    private readonly KafkaTopic _topic;
    private readonly LazyKafkaProducer _producer;
    private readonly bool _fixedDestination;

    public InlineKafkaSender(KafkaTopic topic, bool fixedDestination = false)
        : this(topic, topic.Parent, fixedDestination)
    {
    }

    // Broker-per-tenant (GH-3303): produce over an explicit transport (a tenant's child transport pointed at
    // its own cluster) rather than always the topic's parent
    internal InlineKafkaSender(KafkaTopic topic, KafkaTransport producerTransport, bool fixedDestination = false)
    {
        _topic = topic;
        _fixedDestination = fixedDestination;
        Destination = topic.Uri;
        _producer = producerTransport.ProducerFor(topic);
    }

    public KafkaProducerOptions Config => _producer.Config;

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }
    public async Task<bool> PingAsync()
    {
        try
        {
            await SendAsync(Envelope.ForPing(Destination));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        var record = await _topic.EnvelopeMapper!.CreateMessage(envelope);

        var topicName = _fixedDestination ? _topic.TopicName : envelope.TopicName ?? _topic.TopicName;
        var producer = await _producer.GetAsync();
        // ProduceAsync only completes once the broker has acked this record. A flush here would also wait on
        // every other in-flight record on this shared producer, defeating its linger and batching (GH-3490)
        await producer.ProduceAsync(record.ToProducerMessage(topicName), CancellationToken.None);
    }
}
