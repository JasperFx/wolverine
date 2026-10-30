using Wolverine.Transports.Sending;

namespace Wolverine.Kafka.Internals;

public class InlineKafkaSender : ISender, IAsyncDisposable
{
    private readonly KafkaTopic _topic;
    private readonly LazyKafkaProducer _producer;
    private readonly bool _fixedDestination;
    private readonly bool _ownsProducer;

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
        _producer = LazyKafkaProducer.For(topic, producerTransport);
        _ownsProducer = true;
    }

    // Always produces to the topic's own name over a producer the transport owns, so disposing this sender
    // leaves the producer open
    internal InlineKafkaSender(KafkaTopic topic, LazyKafkaProducer sharedProducer)
    {
        _topic = topic;
        _fixedDestination = true;
        Destination = topic.Uri;
        _producer = sharedProducer;
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
        // ProduceAsync only completes once the broker has acked this record, so a flush here
        // would be redundant for this message — and worse, it would wait on every OTHER in-flight
        // record from concurrent sends on this shared producer, defeating the producer's
        // linger/batching entirely (GH-3490).
        await producer.ProduceAsync(record.ToProducerMessage(topicName), CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        return _ownsProducer ? _producer.DisposeAsync() : ValueTask.CompletedTask;
    }
}
