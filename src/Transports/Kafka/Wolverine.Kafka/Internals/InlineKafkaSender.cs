using Confluent.Kafka;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka.Internals;

public class InlineKafkaSender : ISender
{
    private readonly KafkaTopic _topic;

    public InlineKafkaSender(KafkaTopic topic)
    {
        _topic = topic;
        Destination = topic.Uri;
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }
    public async Task<bool> PingAsync()
    {
        throw new NotImplementedException();
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        var message = _topic.Mapper.CreateMessage(envelope);
        using var producer = new ProducerBuilder<string, string>(_topic.Parent.ProducerConfig).Build();
        await producer.ProduceAsync(envelope.TopicName ?? _topic.TopicName, message);
    }
}