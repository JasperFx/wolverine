using Dekaf.Consumer;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// The Wolverine listener over a Dekaf consumer. Handles both a single-topic endpoint
/// (<see cref="KafkaTopic"/>) and a multi-topic group (<see cref="KafkaTopicGroup"/>) — the
/// subscription is just one consumer over one or more topic names.
/// </summary>
public class KafkaListener : IListener, ISupportDeadLetterQueue, IReportReceiveLoopHealth,
    IReportConnectionState
{
    // How long a single poll waits for a record before the consume loop just loops again. Short enough
    // to keep the receive loop's heartbeat fresh and observe cancellation promptly.
    private static readonly TimeSpan _pollTimeout = TimeSpan.FromSeconds(1);

    // Budget for one consumer-initialization attempt. The consumer is built unconnected so hosts can
    // start while the broker is down; each failed attempt degrades the connection state and the
    // receive loop backs off and retries.
    private static readonly TimeSpan _initializeTimeout = TimeSpan.FromSeconds(10);
    private bool _initialized;

    // GH-3454: degrade-only connection state derived from consume exceptions; a successful
    // consume clears back to Unknown. Never Connected — see KafkaConnectionStateTracker.
    private readonly KafkaConnectionStateTracker _connectionState;
    private readonly KafkaTopic _endpoint;
    private readonly IKafkaConsumer<string, byte[]> _consumer;
    private CancellationTokenSource _cancellation = new();

    // Captured once: DeferAsync() can run after _cancellation is disposed, and a token taken from a disposed
    // source throws. The captured token still reports the cancellation that always precedes the dispose.
    private readonly CancellationToken _stopping;
    // GH-3236: the consume loop runs on the shared BackgroundReceiveLoop (backoff on consume errors,
    // plus a heartbeat + fault detection). Offset flush + consumer close still happen in
    // StopAsync/Dispose AFTER the loop has fully exited, so consumer access stays single-threaded (GH-3150).
    private readonly BackgroundReceiveLoop _loop;
    private readonly IReceiver _receiver;
    private readonly string? _messageTypeName;
    private readonly ILogger _logger;
    private readonly KafkaOffsetCommitter _committer;
    // GH-3434: bounded drain budget for shutdown so StopAsync/Dispose can never block forever waiting on
    // a consume loop that hasn't observed cancellation. Sourced from DurabilitySettings.DrainTimeout
    // (default 30s), matching the SQS and RDBMS listeners.
    private readonly TimeSpan _drainTimeout;
    // Broker-per-tenant (GH-3303): the cluster this listener's DLQ records are produced to. Null for the shared
    // (default-cluster) listener, which falls back to the topic's parent transport.
    private readonly KafkaTransport? _tenantTransport;
    // GH-4422: set when the flush and close blew through the drain budget and were abandoned. The close may
    // still be running, so the consumer must not be disposed underneath it.
    private volatile bool _teardownAbandoned;
    // Gives a record without an id of its own the id of its position in the log, so the durable inbox sees a
    // redelivery of it as the duplicate it is. Null for an endpoint without an inbox to deduplicate against.
    private readonly KafkaRecordIdentity? _recordIdentity;

    public KafkaListener(KafkaTopic topic, KafkaConsumerOptions config,
        IKafkaConsumer<string, byte[]> consumer, string[] topicNames, IReceiver receiver,
        ILogger<KafkaListener> logger, TimeSpan drainTimeout, KafkaTransport? tenantTransport = null,
        KafkaConnectionStateTracker? connectionState = null)
    {
        _endpoint = topic;
        _logger = logger;
        _drainTimeout = drainTimeout;
        _tenantTransport = tenantTransport;
        Address = topic.Uri;
        _consumer = consumer;

        _connectionState = connectionState ?? new KafkaConnectionStateTracker();

        _messageTypeName = topic.MessageType?.ToMessageTypeName();

        // Only the durable inbox deduplicates on the envelope id. Anywhere else a derived id would be a hash per
        // record on the hot path that buys nothing.
        _recordIdentity = topic.Mode == EndpointMode.Durable
            ? KafkaRecordIdentity.For(topic.Parent, tenantTransport, config)
            : null;

        Config = config;
        _receiver = receiver;
        _stopping = _cancellation.Token;
        _committer = new KafkaOffsetCommitter(consumer, config, topic.CommitMode, topic.CommitBatchCount,
            topic.CommitBatchInterval, logger);

        _consumer.Subscribe(topicNames);
        _loop = new BackgroundReceiveLoop(Address, logger, consumeOnceAsync, _cancellation.Token);
        _loop.Start();
    }

    // One consume-and-process iteration. ConsumeOneAsync waits up to the poll timeout for a record; a
    // null result means a quiet topic, so just loop. An OperationCanceledException ends the loop; any
    // other consume error updates the connection state and flows to BackgroundReceiveLoop's
    // log -> backoff -> continue. A processing error AFTER a successful consume is a poison pill —
    // advance past its offset and continue, exactly as before.
    //
    // GH-3490: durable endpoints additionally drain whatever records the consumer has already
    // fetched (up to MaximumMessagesToReceive) and hand them to the receiver as one array, so
    // the durable inbox persists them with a single batched insert instead of gating the
    // consume loop on one INSERT round trip per record.
    private async Task<bool> consumeOnceAsync(CancellationToken token)
    {
        if (!_initialized)
        {
            try
            {
                using var initTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                initTimeout.CancelAfter(_initializeTimeout);
                await _consumer.InitializeAsync(initTimeout.Token);
                _initialized = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // shutting down — let the loop stop
                throw;
            }
            catch (Exception e)
            {
                // Broker unreachable (or slow): degrade the connection state and let the receive
                // loop back off and retry, so the host stays up
                _connectionState.ApplyException(e);
                throw;
            }
        }

        ConsumeResult<string, byte[]>? maybe;
        try
        {
            maybe = await _consumer.ConsumeOneAsync(_pollTimeout, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _connectionState.ApplyException(e);
            throw;
        }

        if (maybe == null)
        {
            // Poll timeout on a quiet topic; nothing to do this iteration
            return true;
        }

        var result = maybe.Value;
        _connectionState.MarkSuccessfulConsume();

        try
        {
            // Retry-tier topics gate on per-record produce timestamps and durable batching only
            // helps inbox-backed endpoints, so everything else keeps strict one-at-a-time consumption.
            if (_endpoint.Mode == EndpointMode.Durable
                && _endpoint.MaximumMessagesToReceive > 1
                && _endpoint.RetryTierDelay == null)
            {
                await consumeManyAsync(result);
            }
            else
            {
                await consumeSingleAsync(result, token);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down mid-process — let the loop stop
            throw;
        }
        catch (Exception e)
        {
            // Might be a poison pill message; advance past its specific offset so we don't get stuck re-consuming it.
            await _committer.Complete(result.Topic, result.Partition, result.Offset);
            _logger.LogError(e, "Error trying to map Kafka message to a Wolverine envelope");
        }

        return true;
    }

    private async Task consumeSingleAsync(ConsumeResult<string, byte[]> result, CancellationToken token)
    {
        // Seed the offset watermark in consume (in-order) order so out-of-order handler completion can never
        // commit past this still-in-flight offset (GH-3161).
        _committer.Track(result.Topic, result.Partition, result.Offset);

        // Non-blocking retry-tier topic (GH-3148): wait out the fixed delay relative to when the record was
        // produced before reprocessing. Records in a tier are time-ordered, so the head record gates the rest.
        if (_endpoint.RetryTierDelay is { } retryDelay)
        {
            var due = result.Timestamp.UtcDateTime + retryDelay;
            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, token);
            }
        }

        var envelope = buildEnvelope(result);
        await _receiver.ReceivedAsync(this, envelope);
    }

    private async Task consumeManyAsync(ConsumeResult<string, byte[]> first)
    {
        var envelopes = new List<Envelope>(Math.Min(_endpoint.MaximumMessagesToReceive, 32));
        var current = first;

        while (true)
        {
            // Track in consume order BEFORE dispatch so the watermark can never commit past an
            // in-flight record (GH-3161), poison pills included.
            _committer.Track(current.Topic, current.Partition, current.Offset);

            try
            {
                envelopes.Add(buildEnvelope(current));
            }
            catch (Exception e)
            {
                // Poison pill mid-batch: advance past its offset, keep the rest of the batch.
                await _committer.Complete(current.Topic, current.Partition, current.Offset);
                _logger.LogError(e, "Error trying to map Kafka message to a Wolverine envelope");
            }

            if (envelopes.Count >= _endpoint.MaximumMessagesToReceive)
            {
                break;
            }

            // Zero-timeout poll: only drains records the consumer already holds locally — never
            // waits on the broker, so a quiet topic still processes each record immediately.
            var next = await _consumer.ConsumeOneAsync(TimeSpan.Zero, CancellationToken.None);
            if (next == null)
            {
                break;
            }

            current = next.Value;
        }

        switch (envelopes.Count)
        {
            case 0:
                return;
            case 1:
                await _receiver.ReceivedAsync(this, envelopes[0]);
                return;
            default:
                await _receiver.ReceivedAsync(this, envelopes.ToArray());
                return;
        }
    }

    private Envelope buildEnvelope(ConsumeResult<string, byte[]> result)
    {
        var envelope = _endpoint.EnvelopeMapper!.CreateEnvelope(result, _recordIdentity);
        envelope.TopicName = result.Topic;
        envelope.Offset = result.Offset;
        envelope.PartitionId = result.Partition;
        envelope.MessageType ??= _messageTypeName;

        if (_endpoint.GroupByMessageKey)
        {
            // GH-3140: shard by-key processing on the Kafka message key.
            envelope.GroupId = result.Key;
        }
        else if (_endpoint.StampConsumerGroupIdOnEnvelope)
        {
            envelope.GroupId = Config.GroupId;
        }

        return envelope;
    }

    public KafkaConsumerOptions Config { get; }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    // GH-3236: surface the consume loop's liveness (heartbeat + faulted/hung detection) for EndpointHealthSnapshot.
    public ReceiveLoopStatus ReceiveLoopStatus => _loop.ReceiveLoopStatus;
    public DateTimeOffset? LastReceiveLoopActivityAt => _loop.LastReceiveLoopActivityAt;

    public TransportConnectionState ConnectionState => _connectionState.ConnectionState;

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope.TopicName != null && envelope.PartitionId.HasValue)
        {
            await _committer.Complete(envelope.TopicName, envelope.PartitionId.Value, envelope.Offset);
        }
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        // Really just a retry, but never inline: see KafkaDeferredRedelivery (GH-5236)
        return KafkaDeferredRedelivery.DeferAsync(this, _receiver, envelope, _stopping, _logger);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _loop.DisposeAsync();
        _cancellation.Dispose();

        if (_teardownAbandoned)
        {
            _logger.LogDebug(
                "Not disposing the Kafka consumer for {Uri} because its close was abandoned and may still be running",
                Address);
            return;
        }

        try
        {
            await _consumer.DisposeAsync();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error disposing Kafka consumer");
        }
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        // Drain the loop within a bounded budget so shutdown can never hang (GH-3434). On a clean drain the
        // consume loop has exited and consumer access is single-threaded before we flush + close (GH-3150).
        await _loop.StopAsync(_drainTimeout);

        // A consumer that never initialized (broker down since startup) has nothing to flush or close
        if (!_initialized)
        {
            return;
        }

        // GH-4422: the flush and close are bounded by the same drain budget, so an unreachable coordinator
        // cannot hold shutdown past DrainTimeout. Anything left uncommitted is redelivered after the rebalance.
        try
        {
            await flushAndCloseAsync().WaitAsync(_drainTimeout);
        }
        catch (TimeoutException)
        {
            _teardownAbandoned = true;
            _logger.LogWarning(
                "{Uri}: the Kafka consumer did not flush offsets and close within the drain timeout of {DrainTimeout}. Abandoning the wait so shutdown can finish; uncommitted offsets will be redelivered.",
                Address, _drainTimeout);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error closing Kafka consumer on shutdown");
        }
    }

    private async Task flushAndCloseAsync()
    {
        await _committer.FlushAsync();
        await _consumer.CloseAsync(CancellationToken.None);
    }

    public bool NativeDeadLetterQueueEnabled => _endpoint.NativeDeadLetterQueueEnabled;

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        // Broker-per-tenant (GH-3303): a tenant listener must DLQ onto its own cluster, not the shared one.
        var transport = _tenantTransport ?? _endpoint.Parent;
        var dlqTopicName = transport.DeadLetterQueueTopicName;

        try
        {
            // Stamp the standard failure metadata (exception info + original
            // destination/partition/offset) so the mapper carries it as Kafka headers. GH-3474
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
            var record = await _endpoint.EnvelopeMapper!.CreateMessage(envelope);

            // ProduceAsync completes on the broker's ack, so the record is durable once it returns
            var producer = await _endpoint.Parent.RecordProducerFor(_endpoint, transport).GetAsync();
            await producer.ProduceAsync(record.ToProducerMessage(dlqTopicName), CancellationToken.None);

            _logger.LogInformation(
                "Moved envelope {EnvelopeId} to dead letter queue topic {DlqTopic}. Exception: {ExceptionType}: {ExceptionMessage}",
                envelope.Id, dlqTopicName, exception.GetType().Name, exception.Message);

            // Advance past the failed message's specific offset now that it's safely in the DLQ.
            if (envelope.TopicName != null && envelope.PartitionId.HasValue)
            {
                await _committer.Complete(envelope.TopicName, envelope.PartitionId.Value, envelope.Offset);
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
}
