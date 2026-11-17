using System.Runtime.ExceptionServices;
using Dekaf;
using Dekaf.Consumer;
using Dekaf.Diagnostics;
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
    IReportConnectionState, IKafkaClientHealthSource
{
    // How long a single poll waits for a record before the consume loop just loops again. Short enough
    // to keep the receive loop's heartbeat fresh and observe cancellation promptly.
    private static readonly TimeSpan _pollTimeout = TimeSpan.FromSeconds(1);

    // Budget for one consumer-initialization attempt. The consumer is built unconnected so hosts can
    // start while the broker is down; each failed attempt degrades the connection state and the
    // receive loop backs off and retries.
    private static readonly TimeSpan _initializeTimeout = TimeSpan.FromSeconds(10);
    private volatile bool _initialized;
    private volatile bool _initializeFailed;

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

    // Retry-tier topics gate on per-record produce timestamps, and batching only helps inbox-backed endpoints, so
    // everything else hands the receiver one record at a time
    private readonly bool _receivesBatches;

    // Retry-tier partitions paused until their head record is due. Only the consume loop touches it
    private readonly Dictionary<TopicPartition, DateTime> _pausedUntil = new();

    public KafkaListener(KafkaTopic topic, KafkaConsumerOptions config,
        IKafkaConsumer<string, byte[]> consumer, string[] topicNames, IReceiver receiver,
        ILogger<KafkaListener> logger, TimeSpan drainTimeout, KafkaTransport? tenantTransport = null)
        : this(topic, config, consumer, topicNames, receiver, logger, drainTimeout, tenantTransport, null)
    {
    }

    // rebalance is the relay the consumer was built with, so revocations reach this listener's committer
    internal KafkaListener(KafkaTopic topic, KafkaConsumerOptions config,
        IKafkaConsumer<string, byte[]> consumer, string[] topicNames, IReceiver receiver,
        ILogger<KafkaListener> logger, TimeSpan drainTimeout, KafkaTransport? tenantTransport,
        KafkaRebalanceRelay? rebalance)
    {
        _endpoint = topic;
        _logger = logger;
        _drainTimeout = drainTimeout;
        _tenantTransport = tenantTransport;
        Address = topic.Uri;
        _consumer = consumer;

        _messageTypeName = topic.MessageType?.ToMessageTypeName();

        // Only the durable inbox deduplicates on the envelope id. Anywhere else a derived id would be a hash per
        // record on the hot path that buys nothing.
        _recordIdentity = topic.Mode == EndpointMode.Durable
            ? KafkaRecordIdentity.For(topic.Parent, tenantTransport, config)
            : null;

        _receivesBatches = topic.Mode == EndpointMode.Durable && topic.MaximumMessagesToReceive > 1 &&
                           topic.RetryTierDelay == null;

        Config = config;
        _receiver = receiver;
        _stopping = _cancellation.Token;
        _committer = new KafkaOffsetCommitter(consumer, config, topic.CommitMode, topic.CommitBatchCount,
            topic.CommitBatchInterval, logger);
        if (rebalance != null)
        {
            rebalance.Committer = _committer;
        }

        _consumer.Subscribe(topicNames);
        _loop = new BackgroundReceiveLoop(Address, logger, consumeOnceAsync, _cancellation.Token);
        _loop.Start();

        // Registered with the transport of the cluster it consumes, for the health check
        clusterTransport.TrackClient(this);
    }

    private KafkaTransport clusterTransport => _tenantTransport ?? _endpoint.Parent;

    // One consume-and-dispatch pass. ConsumeOneAsync waits up to the poll timeout for a record; a null result means
    // a quiet topic, so just loop. An OperationCanceledException from shutdown ends the loop; any other consume
    // error flows to BackgroundReceiveLoop's log, backoff and retry.
    //
    // Every record goes through tryBuild: tracked so no commit passes it, then mapped. A record that can't be mapped
    // is a poison pill and is completed so its partition moves on.
    //
    // GH-3490: durable endpoints additionally drain whatever records the consumer has already fetched (up to
    // MaximumMessagesToReceive) and hand them to the receiver as one array, so the durable inbox persists them with
    // a single batched insert instead of gating the consume loop on one INSERT round trip per record.
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
                // shutting down, let the loop stop
                throw;
            }
            catch (Exception)
            {
                // Broker unreachable (or slow): report it and let the receive loop back off and retry, so the
                // host stays up
                _initializeFailed = true;
                throw;
            }
        }

        // Commits what handlers finished since the last pass, here on the loop that owns the consumer
        await _committer.CommitCompletedAsync();
        resumeDuePartitions();

        var maybe = await _consumer.ConsumeOneAsync(pollTimeout(), token);
        if (maybe == null)
        {
            // Poll timeout on a quiet topic; nothing to do this iteration
            return true;
        }

        var record = maybe.Value;
        if (_endpoint.RetryTierDelay is { } retryDelay && deferUntilDue(record, retryDelay))
        {
            return true;
        }

        if (!_receivesBatches)
        {
            if (tryBuild(record) is { } envelope)
            {
                await dispatchAsync(envelope, token);
            }

            return true;
        }

        var envelopes = new List<Envelope>(Math.Min(_endpoint.MaximumMessagesToReceive, 32));
        Exception? consumeFailure = null;
        while (true)
        {
            if (tryBuild(record) is { } envelope)
            {
                envelopes.Add(envelope);
                if (envelopes.Count >= _endpoint.MaximumMessagesToReceive) break;
            }

            // Zero-timeout poll: only drains records the consumer already holds locally, never waits on the broker,
            // so a quiet topic still processes each record immediately
            ConsumeResult<string, byte[]>? next;
            try
            {
                next = await _consumer.ConsumeOneAsync(TimeSpan.Zero, CancellationToken.None);
            }
            catch (Exception e)
            {
                // The records already taken are tracked, so they are dispatched before the failure reaches the loop
                consumeFailure = e;
                break;
            }

            if (next == null) break;
            record = next.Value;
        }

        await dispatchAsync(envelopes, token);

        if (consumeFailure != null)
        {
            ExceptionDispatchInfo.Throw(consumeFailure);
        }

        return true;
    }

    // Tracks the record so no commit passes it, then maps it. Null for a poison pill, which is completed instead.
    private Envelope? tryBuild(ConsumeResult<string, byte[]> record)
    {
        _committer.Track(record.Topic, record.Partition, record.Offset);

        try
        {
            return buildEnvelope(record);
        }
        catch (Exception e)
        {
            _committer.Complete(record.Topic, record.Partition, record.Offset);
            _logger.LogError(e, "Error trying to map Kafka message to a Wolverine envelope");
            return null;
        }
    }

    private async Task dispatchAsync(Envelope envelope, CancellationToken token)
    {
        try
        {
            await _receiver.ReceivedAsync(this, envelope);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            completeUndelivered(envelope);
            _logger.LogError(e, "The receiver for {Uri} rejected envelope {EnvelopeId}, so its record is skipped",
                Address, envelope.Id);
        }
    }

    private async Task dispatchAsync(List<Envelope> envelopes, CancellationToken token)
    {
        switch (envelopes.Count)
        {
            case 0:
                return;
            case 1:
                await dispatchAsync(envelopes[0], token);
                return;
        }

        try
        {
            await _receiver.ReceivedAsync(this, envelopes.ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            foreach (var envelope in envelopes) completeUndelivered(envelope);
            _logger.LogError(e, "The receiver for {Uri} rejected a batch of {Count} envelopes, so their records are skipped",
                Address, envelopes.Count);
        }
    }

    // A record the receiver never took is completed like a poison pill, so its partition is not held at it forever
    private void completeUndelivered(Envelope envelope)
    {
        _committer.Complete(envelope.TopicName!, envelope.PartitionId!.Value, envelope.Offset);
    }

    // Non-blocking retry-tier topic (GH-3148): a record is due its fixed delay after it was produced. Records in a
    // tier are time-ordered, so one that is not due yet holds back its own partition only: the partition is paused and
    // rewound to the record, which is delivered again once resumeDuePartitions lets the partition go.
    private bool deferUntilDue(ConsumeResult<string, byte[]> record, TimeSpan retryDelay)
    {
        var due = record.Timestamp.UtcDateTime + retryDelay;
        if (due <= DateTime.UtcNow)
        {
            return false;
        }

        var partition = new TopicPartition(record.Topic, record.Partition);
        _consumer.Partitions.Pause(partition);
        _consumer.Positions.Seek(new TopicPartitionOffset(record.Topic, record.Partition, record.Offset));
        _pausedUntil[partition] = due;
        return true;
    }

    private void resumeDuePartitions()
    {
        if (_pausedUntil.Count == 0) return;

        var now = DateTime.UtcNow;
        List<TopicPartition>? due = null;
        foreach (var (partition, until) in _pausedUntil)
        {
            if (until <= now) (due ??= []).Add(partition);
        }

        if (due == null) return;

        foreach (var partition in due) _pausedUntil.Remove(partition);
        _consumer.Partitions.Resume(due.ToArray());
    }

    // Wakes the loop when the next paused retry-tier partition is due rather than up to a poll timeout later
    private TimeSpan pollTimeout()
    {
        if (_pausedUntil.Count == 0) return _pollTimeout;

        var next = _pausedUntil.Values.Min() - DateTime.UtcNow;
        return next <= TimeSpan.Zero ? TimeSpan.Zero : next < _pollTimeout ? next : _pollTimeout;
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

    // GH-3454: the consumer's own broker connection status. Before it first connects there is none, so a failed
    // initialization reports Disconnected.
    public TransportConnectionState ConnectionState
    {
        get
        {
            if (!_initialized)
            {
                return _initializeFailed ? TransportConnectionState.Disconnected : TransportConnectionState.Unknown;
            }

            var status = ((IKafkaClientHealthSource)this).Status;
            return status == null ? TransportConnectionState.Unknown : KafkaConnectionState.From(status);
        }
    }

    KafkaClientStatus? IKafkaClientHealthSource.Status =>
        _initialized ? (_consumer as IKafkaClientStatusProvider)?.GetStatus() : null;

    bool IKafkaClientHealthSource.FailedToConnect => !_initialized && _initializeFailed;

    /// <summary>
    /// Records waiting on the broker across this consumer's assigned partitions, as of its latest fetches. Null
    /// until the lag of at least one partition is known.
    /// </summary>
    internal long? CurrentLag()
    {
        if (!_initialized || _consumer is not IConsumerLag lag)
        {
            return null;
        }

        long? total = null;
        foreach (var partition in _consumer.Assignment)
        {
            if (lag.GetCurrentLag(partition) is { } partitionLag)
            {
                total = (total ?? 0) + partitionLag;
            }
        }

        return total;
    }

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
        // Really just a retry, but never inline: see KafkaDeferredRedelivery (GH-5236)
        return KafkaDeferredRedelivery.DeferAsync(this, _receiver, envelope, _stopping, _logger);
    }

    public async ValueTask DisposeAsync()
    {
        clusterTransport.ForgetClient(this);
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
        var transport = clusterTransport;
        var dlqTopicName = transport.DeadLetterQueueTopicName;

        try
        {
            // Stamp the standard failure metadata (exception info + original
            // destination/partition/offset) so the mapper carries it as Kafka headers. GH-3474
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
            var record = await _endpoint.EnvelopeMapper!.CreateMessage(envelope);

            // ProduceAsync completes on the broker's ack, so the record is durable once it returns
            var producer = await transport.ProducerFor(_endpoint).GetAsync();
            await producer.ProduceAsync(record.ToProducerMessage(dlqTopicName), CancellationToken.None);

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
}
