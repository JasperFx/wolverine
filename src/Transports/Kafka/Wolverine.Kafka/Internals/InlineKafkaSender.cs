using Dekaf.Producer;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka.Internals;

public class InlineKafkaSender : ISender, IAsyncDisposable
{
    private readonly KafkaTopic _topic;
    // Dekaf producers are built and initialized asynchronously, but senders are constructed on
    // synchronous paths, so the producer is created lazily on first send.
    private readonly KafkaTransport _producerTransport;
    private readonly SemaphoreSlim _producerLock = new(1, 1);
    private IKafkaProducer<string, byte[]>? _producer;
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
        _producerTransport = producerTransport;
    }

    private async ValueTask<IKafkaProducer<string, byte[]>> producerAsync()
    {
        if (_producer != null) return _producer;

        await _producerLock.WaitAsync();
        try
        {
            return _producer ??= await _producerTransport.CreateProducerAsync(Config);
        }
        finally
        {
            _producerLock.Release();
        }
    }

    public KafkaProducerOptions Config { get; }

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
        var producer = await producerAsync();
        // ProduceAsync only completes once the broker has acked this record, so a flush here
        // would be redundant for this message — and worse, it would wait on every OTHER in-flight
        // record from concurrent sends on this shared producer, defeating the producer's
        // linger/batching entirely (GH-3490).
        await producer.ProduceAsync(record.ToProducerMessage(topicName), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        var producer = _producer;
        _producerLock.Dispose();
        if (producer == null)
        {
            return;
        }

        // Bounded drain for anything still queued in the producer (e.g. a send abandoned by a
        // cancelled caller) before tearing the producer down. DisposeAsync flushes pending records.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await producer.FlushAsync(timeout.Token);
            await producer.DisposeAsync();
        }
        catch (Exception)
        {
            // already gone or the drain timed out — nothing more to do
        }
    }
}
