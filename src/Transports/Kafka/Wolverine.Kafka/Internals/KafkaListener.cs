using System.Text;
using Confluent.Kafka;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Kafka.Internals;

public class KafkaListener : IListener, IDisposable, ISupportDeadLetterQueue, IReportReceiveLoopHealth
{
    private readonly KafkaTopic _endpoint;
    private readonly IConsumer<string, byte[]> _consumer;
    private CancellationTokenSource _cancellation = new();
    // GH-3236: the consume loop now runs on the shared BackgroundReceiveLoop (backoff on Consume errors instead of
    // the previous hot-loop, plus a heartbeat + fault detection). Offset flush + consumer close still happen in
    // StopAsync/Dispose AFTER the loop has fully exited, so consumer access stays single-threaded (GH-3150).
    private readonly BackgroundReceiveLoop _loop;
    private readonly IReceiver _receiver;
    private readonly string? _messageTypeName;
    private readonly ILogger _logger;
    private readonly KafkaOffsetCommitter _committer;
    // GH-3434: bounded drain budget for shutdown so StopAsync/Dispose can never block forever waiting on a
    // consume loop whose blocking IConsumer.Consume(token) hasn't observed cancellation. Sourced from
    // DurabilitySettings.DrainTimeout (default 30s), matching the SQS and RDBMS listeners.
    private readonly TimeSpan _drainTimeout;
    // Broker-per-tenant (GH-3303): the cluster this listener's DLQ records are produced to. Null for the shared
    // (default-cluster) listener, which falls back to the topic's parent transport.
    private readonly KafkaTransport? _tenantTransport;

    public KafkaListener(KafkaTopic topic, ConsumerConfig config,
        IConsumer<string, byte[]> consumer, IReceiver receiver,
        ILogger<KafkaListener> logger, TimeSpan drainTimeout, KafkaTransport? tenantTransport = null)
    {
        _endpoint = topic;
        _logger = logger;
        _drainTimeout = drainTimeout;
        _tenantTransport = tenantTransport;
        Address = topic.Uri;
        _consumer = consumer;

        _messageTypeName = topic.MessageType?.ToMessageTypeName();

        Config = config;
        _receiver = receiver;
        _committer = new KafkaOffsetCommitter(consumer, config, topic.CommitMode, topic.CommitBatchCount,
            topic.CommitBatchInterval, logger);

        _consumer.Subscribe(topic.TopicName);
        _loop = new BackgroundReceiveLoop(Address, logger, consumeOnceAsync, _cancellation.Token);
        _loop.Start();
    }

    // One consume-and-process iteration. _consumer.Consume blocks until a record (or throws). An
    // OperationCanceledException ends the loop; any other Consume error flows to BackgroundReceiveLoop's
    // log -> backoff -> continue (previously this hot-looped on every error). A processing error AFTER a
    // successful consume is a poison pill — advance past its offset and continue, exactly as before.
    private async Task<bool> consumeOnceAsync(CancellationToken token)
    {
        var result = _consumer.Consume(token);

        try
        {
            var message = result.Message;

            // Seed the offset watermark in consume (in-order) order so out-of-order handler completion can never
            // commit past this still-in-flight offset (GH-3161).
            _committer.Track(result.Topic, result.Partition.Value, result.Offset.Value);

            // Non-blocking retry-tier topic (GH-3148): wait out the fixed delay relative to when the record was
            // produced before reprocessing. Records in a tier are time-ordered, so the head record gates the rest.
            if (_endpoint.RetryTierDelay is { } retryDelay)
            {
                var due = message.Timestamp.UtcDateTime + retryDelay;
                var wait = due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, token);
                }
            }

            var envelope = _endpoint.EnvelopeMapper!.CreateEnvelope(result.Topic, message);
            envelope.TopicName = result.Topic;
            envelope.Offset = result.Offset.Value;
            envelope.PartitionId = result.Partition.Value;
            envelope.MessageType ??= _messageTypeName;

            if (_endpoint.GroupByMessageKey)
            {
                // GH-3140: shard by-key processing on the Kafka message key.
                envelope.GroupId = message.Key;
            }
            else if (_endpoint.StampConsumerGroupIdOnEnvelope)
            {
                envelope.GroupId = Config.GroupId;
            }

            await _receiver.ReceivedAsync(this, envelope);
        }
        catch (OperationCanceledException)
        {
            // shutting down mid-process — let the loop stop
            throw;
        }
        catch (Exception e)
        {
            // Might be a poison pill message; advance past its specific offset so we don't get stuck re-consuming it.
            _committer.Complete(result.Topic, result.Partition.Value, result.Offset.Value);
            _logger.LogError(e, "Error trying to map Kafka message to a Wolverine envelope");
        }

        return true;
    }

    public ConsumerConfig Config { get; }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    // GH-3236: surface the consume loop's liveness (heartbeat + faulted/hung detection) for EndpointHealthSnapshot.
    public ReceiveLoopStatus ReceiveLoopStatus => _loop.ReceiveLoopStatus;
    public DateTimeOffset? LastReceiveLoopActivityAt => _loop.LastReceiveLoopActivityAt;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope.TopicName != null && envelope.PartitionId.HasValue)
        {
            _committer.Complete(envelope.TopicName, envelope.PartitionId.Value, envelope.Offset);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        // Really just a retry
        return _receiver.ReceivedAsync(this, envelope);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _loop.DisposeAsync();
        _cancellation.Dispose();
        _consumer.SafeDispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        // Drain the loop within a bounded budget so shutdown can never hang (GH-3434). On a clean drain the consume
        // loop has exited and consumer access is single-threaded before we flush + close (GH-3150). If the loop is
        // wedged in a blocking Consume that ignored cancellation, the drain logs and returns, and the _consumer.Close()
        // below forces that Consume to unwind — bounded teardown instead of an infinite await.
        await _loop.StopAsync(_drainTimeout);

        _committer.Flush();
        try
        {
            _consumer.Close();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error closing Kafka consumer on shutdown");
        }
    }

    public bool NativeDeadLetterQueueEnabled => _endpoint.NativeDeadLetterQueueEnabled;

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        // Broker-per-tenant (GH-3303): a tenant listener must DLQ onto its own cluster, not the shared one.
        var transport = _tenantTransport ?? _endpoint.Parent;
        var dlqTopicName = transport.DeadLetterQueueTopicName;
        var producerConfig = _tenantTransport != null
            ? _tenantTransport.ProducerConfig
            : _endpoint.GetEffectiveProducerConfig();

        try
        {
            var message = await _endpoint.EnvelopeMapper!.CreateMessage(envelope);

            message.Headers ??= new Headers();
            message.Headers.Add(DeadLetterQueueConstants.ExceptionTypeHeader, Encoding.UTF8.GetBytes(exception.GetType().FullName ?? "Unknown"));
            message.Headers.Add(DeadLetterQueueConstants.ExceptionMessageHeader, Encoding.UTF8.GetBytes(exception.Message));
            message.Headers.Add(DeadLetterQueueConstants.ExceptionStackHeader, Encoding.UTF8.GetBytes(exception.StackTrace ?? ""));
            message.Headers.Add(DeadLetterQueueConstants.FailedAtHeader, Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));

            using var producer = transport.CreateProducer(producerConfig);
            await producer.ProduceAsync(dlqTopicName, message);
            producer.Flush();

            _logger.LogInformation(
                "Moved envelope {EnvelopeId} to dead letter queue topic {DlqTopic}. Exception: {ExceptionType}: {ExceptionMessage}",
                envelope.Id, dlqTopicName, exception.GetType().Name, exception.Message);

            // Advance past the failed message's specific offset now that it's safely in the DLQ.
            if (envelope.TopicName != null && envelope.PartitionId.HasValue)
            {
                _committer.Complete(envelope.TopicName, envelope.PartitionId.Value, envelope.Offset);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to move envelope {EnvelopeId} to dead letter queue topic {DlqTopic}",
                envelope.Id, dlqTopicName);
            throw;
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        _loop.StopAsync(_drainTimeout).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
        _committer.Flush();
        _cancellation.Dispose();
        _consumer.SafeDispose();
    }
}
