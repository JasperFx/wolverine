using Confluent.Kafka;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka.Internals;

public class InlineKafkaSender : ISender, IDisposable
{
    private readonly KafkaTopic _topic;
    private readonly IProducer<string, byte[]> _producer;

    public InlineKafkaSender(KafkaTopic topic)
    {
        _topic = topic;
        Destination = topic.Uri;
        Config = topic.GetEffectiveProducerConfig();
        _producer = _topic.Parent.CreateProducer(Config);
    }

    public ProducerConfig Config { get; }

    public bool SupportsNativeScheduledSend => false;
    public bool SupportsNativeScheduledCancellation => false;
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

        await _producer.ProduceAsync(envelope.TopicName ?? _topic.TopicName, message);
        _producer.Flush();
    }

    public void Dispose()
    {
        _producer.Dispose();
    }
}