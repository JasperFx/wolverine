using Confluent.Kafka;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka.Internals;

public class InlineKafkaSender : ISender, IDisposable
{
    private readonly KafkaTopic _topic;
    private readonly IProducer<string,string> _producer;

    public InlineKafkaSender(KafkaTopic topic)
    {
        _topic = topic;
        Destination = topic.Uri;
        _producer = new ProducerBuilder<string, string>(_topic.Parent.ProducerConfig).Build();
        
    }

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

    public ValueTask SendAsync(Envelope envelope)
    {
        var message = _topic.Mapper.CreateMessage(envelope);
        
        return new ValueTask(_producer.ProduceAsync(envelope.TopicName ?? _topic.TopicName, message));
    }

    public void Dispose()
    {
        _producer.Dispose();
    }
}