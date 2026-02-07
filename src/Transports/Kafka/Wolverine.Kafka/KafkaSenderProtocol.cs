using Confluent.Kafka;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka;

public class KafkaSenderProtocol : ISenderProtocol, IDisposable
{
    private readonly KafkaTopic _topic;
    private readonly IProducer<string, byte[]> _producer;

    public KafkaSenderProtocol(KafkaTopic topic)
    {
        _topic = topic;
        _producer = _topic.Parent.CreateProducer(_topic.GetEffectiveProducerConfig());
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        foreach (var envelope in batch.Messages)
        {
            // TODO -- separate try/catch here!

            var message = await _topic.EnvelopeMapper!.CreateMessage(envelope);
            await _producer.ProduceAsync(envelope.TopicName ?? _topic.TopicName, message);
        }

        _producer.Flush();

        await callback.MarkSuccessfulAsync(batch);
    }

    public void Dispose()
    {
        _producer.Dispose();
    }
}