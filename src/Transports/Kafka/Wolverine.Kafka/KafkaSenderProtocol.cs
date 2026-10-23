using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka;

public class KafkaSenderProtocol : ISenderProtocol, IDisposable
{
    private readonly KafkaTopic _topic;
    private readonly IProducer<string, byte[]> _producer;

    /// <summary>
    ///     Where an envelope the mapper throws on is logged with the original exception
    /// </summary>
    internal ILogger Logger { get; set; } = NullLogger.Instance;

    public KafkaSenderProtocol(KafkaTopic topic)
        : this(topic, topic.Parent)
    {
    }

    // Broker-per-tenant (GH-3303): batch over an explicit transport (a tenant's child transport pointed at its
    // own cluster) rather than always the topic's parent. When it *is* the parent, behaves exactly as before.
    internal KafkaSenderProtocol(KafkaTopic topic, KafkaTransport producerTransport)
    {
        _topic = topic;
        var config = ReferenceEquals(producerTransport, topic.Parent)
            ? topic.GetEffectiveProducerConfig()
            : producerTransport.ProducerConfig;
        _producer = producerTransport.CreateProducer(config);
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
        // If every send acks, MarkSuccessfulAsync for the batch. If any fails,
        // the acked records are reported successful and only the rest failed,
        // each as its own OutgoingMessageBatch, the way the SQS sender does.

        // Phase 1 — map every envelope to a Confluent Message<>. GH-5055: an
        // envelope the mapper throws on can never be sent, so only that one is
        // reported as a serialization failure, which dead letters it. Reporting
        // the whole batch took every envelope that mapped fine down with it
        var prepared = new List<(Envelope envelope, Message<string, byte[]> message)>(batch.Messages.Count);
        List<Envelope>? unmappable = null;
        foreach (var envelope in batch.Messages)
        {
            try
            {
                var message = await _topic.EnvelopeMapper!.CreateMessage(envelope);
                prepared.Add((envelope, message));
            }
            catch (Exception e)
            {
                Logger.LogError(e,
                    "Error while mapping envelope {Envelope} to a Kafka message for {Uri}. It can never be sent, so it is reported as a serialization failure",
                    envelope, _topic.Uri);
                (unmappable ??= new()).Add(envelope);
            }
        }

        if (unmappable != null)
        {
            await callback.MarkSerializationFailureAsync(new OutgoingMessageBatch(batch.Destination, unmappable));

            if (prepared.Count == 0) return;

            batch = new OutgoingMessageBatch(batch.Destination, prepared.Select(x => x.envelope).ToList());
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
            // Each record is acked on its own, so report the ones the broker did ack as sent. Failing the whole
            // batch would send those again on retry, and a record that can never succeed (one over
            // message.max.bytes) would duplicate its siblings on every retry
            var succeeded = new List<Envelope>();
            var failed = new List<Envelope>();
            for (var i = 0; i < prepared.Count; i++)
            {
                (produceTasks[i].IsCompletedSuccessfully ? succeeded : failed).Add(prepared[i].envelope);
            }

            if (succeeded.Count > 0)
            {
                await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, succeeded));
            }

            await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, failed), ex);
            return;
        }

        await callback.MarkSuccessfulAsync(batch);
    }

    public void Dispose()
    {
        _producer.Dispose();
    }
}
