using Confluent.Kafka;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka;

public class KafkaSenderProtocol : ISenderProtocol
{
    private readonly KafkaTopic _topic;

    public KafkaSenderProtocol(KafkaTopic topic)
    {
        _topic = topic;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        using var producer = new ProducerBuilder<string, string>(_topic.Parent.ProducerConfig).Build();

        var tasks = new List<Task>();
        foreach (var envelope in batch.Messages)
        {
            // TODO -- separate try/catch here!

            var message = _topic.Mapper.CreateMessage(envelope);
            var task = producer.ProduceAsync(envelope.TopicName ?? _topic.TopicName, message);
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
        
        await callback.MarkSuccessfulAsync(batch);
    }
}