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
        // wolverine#3076 — fire every produce call before awaiting any of them
        // so the Confluent producer's internal batching window (linger.ms,
        // batch.size, compression) actually has a population to coalesce.
        // The previous implementation awaited each ProduceAsync inline, which
        // forced a broker round-trip per message and capped throughput at the
        // network RTT — under a durable outbox driving 100k+ messages the
        // serialized awaits dwarfed the on-broker work the producer settings
        // were tuned for.
        //
        // Batch semantics stay all-or-nothing to fit ISenderCallback's
        // batch-shaped API: if every send acks, MarkSuccessfulAsync; if any
        // fails, MarkProcessingFailureAsync (the durable outbox layer will
        // retry the batch, idempotency on the receive side dedupes). Per-
        // envelope partial-success bookkeeping would need a callback-shape
        // change that's out of scope here — matches the SQS sender's choice.

        // Phase 1 — map every envelope to a Confluent Message<>. A mapping /
        // serialization failure on any envelope before we've started enqueuing
        // means we haven't put anything on the wire yet — treat as a clean
        // serialization failure on the batch.
        var prepared = new List<(Envelope envelope, Message<string, byte[]> message)>(batch.Messages.Count);
        try
        {
            foreach (var envelope in batch.Messages)
            {
                var message = await _topic.EnvelopeMapper!.CreateMessage(envelope);
                prepared.Add((envelope, message));
            }
        }
        catch (Exception)
        {
            await callback.MarkSerializationFailureAsync(batch);
            return;
        }

        // Phase 2 — enqueue every produce without awaiting so the Confluent
        // producer can fill its internal accumulator before any broker round-
        // trip starts. ProduceAsync returns a Task that completes on broker
        // ack; capturing them up front and awaiting Task.WhenAll lets the
        // producer batch the underlying ProduceRequest across the lot of
        // them rather than fanning one network call per message.
        var produceTasks = new List<Task<DeliveryResult<string, byte[]>>>(prepared.Count);
        foreach (var (envelope, message) in prepared)
        {
            produceTasks.Add(_producer.ProduceAsync(envelope.TopicName ?? _topic.TopicName, message));
        }

        try
        {
            await Task.WhenAll(produceTasks);
        }
        catch (Exception ex)
        {
            // Task.WhenAll surfaces only the first exception when multiple
            // tasks faulted; the caller's failure recovery path treats the
            // batch atomically anyway, so the first cause is enough context.
            await callback.MarkProcessingFailureAsync(batch, ex);
            return;
        }

        await callback.MarkSuccessfulAsync(batch);
    }

    public void Dispose()
    {
        _producer.Dispose();
    }
}
