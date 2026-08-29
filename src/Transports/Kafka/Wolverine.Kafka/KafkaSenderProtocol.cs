using Dekaf.Producer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka;

// IDisposable rather than IAsyncDisposable because BatchedSender only disposes its protocol
// through the synchronous IDisposable check
public class KafkaSenderProtocol : ISenderProtocol, IDisposable
{
    private readonly KafkaTopic _topic;
    // Dekaf producers are built and initialized asynchronously, but sender protocols are constructed
    // on synchronous paths, so the producer is created lazily on first send.
    private readonly KafkaTransport _producerTransport;
    private readonly KafkaProducerOptions _config;
    private readonly SemaphoreSlim _producerLock = new(1, 1);
    private IKafkaProducer<string, byte[]>? _producer;

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
        _config = ReferenceEquals(producerTransport, topic.Parent)
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
            return _producer ??= await _producerTransport.CreateProducerAsync(_config);
        }
        finally
        {
            _producerLock.Release();
        }
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        // wolverine#3076 — hand the whole batch to the producer at once so its internal batching
        // window (linger, batch size, compression) has a population to coalesce, instead of one
        // broker round-trip per message. ProduceAllAsync is Dekaf's sanctioned fan-out API (its
        // ProduceAsync ValueTasks must not be collected and awaited later).
        //
        // Batch semantics for the produce stay all-or-nothing: ProduceAllAsync reports one aggregate
        // failure rather than a per-record outcome, so if every send acks, MarkSuccessfulAsync; if any
        // fails, MarkProcessingFailureAsync (the durable outbox layer will retry the batch, idempotency on
        // the receive side dedupes).

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

        // Phase 2 — produce the lot; ProduceAllAsync completes when every record is acked.
        try
        {
            var producer = await producerAsync();
            await producer.ProduceAllAsync(messages, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The caller's failure recovery path treats the batch atomically anyway, so the first
            // cause is enough context.
            await callback.MarkProcessingFailureAsync(batch, ex);
            return;
        }

        await callback.MarkSuccessfulAsync(batch);
    }

    public void Dispose()
    {
        var producer = _producer;
        if (producer == null)
        {
            return;
        }

        try
        {
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            producer.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
        }
        catch (Exception)
        {
            // already gone — nothing to drain
        }
    }
}
