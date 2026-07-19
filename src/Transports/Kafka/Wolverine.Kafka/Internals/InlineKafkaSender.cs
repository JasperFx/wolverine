using Confluent.Kafka;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka.Internals;

public class InlineKafkaSender : ISender, IDisposable
{
    private readonly KafkaTopic _topic;
    private readonly IProducer<string, byte[]> _producer;
    private readonly bool _fixedDestination;

    public InlineKafkaSender(KafkaTopic topic, bool fixedDestination = false)
        : this(topic, topic.Parent, fixedDestination)
    {
    }

    // Broker-per-tenant (GH-3303): produce over an explicit transport (a tenant's child transport pointed at
    // its own cluster) rather than always the topic's parent. When it *is* the parent, behaves exactly as
    // before, honoring any topic-level producer overrides via GetEffectiveProducerConfig().
    internal InlineKafkaSender(KafkaTopic topic, KafkaTransport producerTransport, bool fixedDestination = false)
    {
        _topic = topic;
        _fixedDestination = fixedDestination;
        Destination = topic.Uri;
        Config = ReferenceEquals(producerTransport, topic.Parent)
            ? topic.GetEffectiveProducerConfig()
            : producerTransport.ProducerConfig;
        _producer = producerTransport.CreateProducer(Config);
    }

    public ProducerConfig Config { get; }

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
        var message = await _topic.EnvelopeMapper!.CreateMessage(envelope);

        var topicName = _fixedDestination ? _topic.TopicName : envelope.TopicName ?? _topic.TopicName;
        // ProduceAsync only completes once the broker has acked this record, so a Flush() here
        // is redundant for this message — and worse, it's an unbounded synchronous block that
        // waits on every OTHER in-flight record from concurrent sends on this shared producer,
        // defeating librdkafka's linger/batching entirely (GH-3490).
        await _producer.ProduceAsync(topicName, message);
    }

    public void Dispose()
    {
        // Bounded drain for anything still queued in librdkafka (e.g. a send abandoned by a
        // cancelled caller) before tearing the producer down.
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(10));
        }
        catch (ObjectDisposedException)
        {
            // already gone — nothing to drain
        }

        _producer.Dispose();
    }
}