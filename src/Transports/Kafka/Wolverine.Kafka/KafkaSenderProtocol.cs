using Dekaf.Producer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka;

public class KafkaSenderProtocol : ISenderProtocol, IAsyncDisposable
{
    private readonly KafkaTopic _topic;
    private readonly LazyKafkaProducer _producer;

    /// <summary>
    ///     Where an envelope the mapper throws on is logged with the original exception
    /// </summary>
    internal ILogger Logger { get; set; } = NullLogger.Instance;

    public KafkaSenderProtocol(KafkaTopic topic)
        : this(topic, topic.Parent)
    {
    }

    // Broker-per-tenant (GH-3303): batch over an explicit transport (a tenant's child transport pointed at its
    // own cluster) rather than always the topic's parent
    internal KafkaSenderProtocol(KafkaTopic topic, KafkaTransport producerTransport)
    {
        _topic = topic;
        _producer = LazyKafkaProducer.For(topic, producerTransport);
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        // wolverine#3076 — hand the whole batch to the producer at once so its internal batching
        // window (linger, batch size, compression) has a population to coalesce, instead of one
        // broker round-trip per message.
        //
        // If every record acks, MarkSuccessfulAsync for the batch. If any fails, the acked records are
        // reported successful and only the rest failed. Failing the whole batch would send the acked records
        // again on retry, and a record that can never succeed (one over message.max.bytes) would duplicate
        // its siblings on every durability pass.

        // Phase 1 — map every envelope to a producer message. GH-5055: an envelope the mapper throws on
        // can never be sent, so only that one is reported as a serialization failure, which dead letters
        // it. Reporting the whole batch took every envelope that mapped fine down with it
        var prepared = new List<Envelope>(batch.Messages.Count);
        var messages = new List<ProducerMessage<string, byte[]>>(batch.Messages.Count);
        List<Envelope>? unmappable = null;
        foreach (var envelope in batch.Messages)
        {
            try
            {
                var record = await _topic.EnvelopeMapper!.CreateMessage(envelope);
                messages.Add(record.ToProducerMessage(envelope.TopicName ?? _topic.TopicName));
                prepared.Add(envelope);
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

            batch = new OutgoingMessageBatch(batch.Destination, prepared);
        }

        // Phase 2 — produce the lot and learn how each record fared
        Exception?[] failures;
        try
        {
            var producer = await _producer.GetAsync();
            failures = await KafkaBatchDelivery.ProduceAsync(producer, messages);
        }
        catch (Exception ex)
        {
            // No producer, so nothing was sent
            await callback.MarkProcessingFailureAsync(batch, ex);
            return;
        }

        List<Envelope>? succeeded = null;
        List<Envelope>? failed = null;
        Exception? firstFailure = null;
        for (var i = 0; i < failures.Length; i++)
        {
            if (failures[i] is { } failure)
            {
                (failed ??= []).Add(prepared[i]);
                firstFailure ??= failure;
            }
            else
            {
                (succeeded ??= []).Add(prepared[i]);
            }
        }

        if (failed == null)
        {
            await callback.MarkSuccessfulAsync(batch);
            return;
        }

        if (succeeded != null)
        {
            await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, succeeded));
        }

        await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, failed), firstFailure);
    }

    public ValueTask DisposeAsync()
    {
        return _producer.DisposeAsync();
    }
}
