using Confluent.Kafka;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Kafka.Internals;

public class KafkaListener : IListener, IDisposable, ISupportDeadLetterQueue, IReportReceiveLoopHealth,
    IReportConnectionState
{
    // GH-3454: degrade-only connection state derived from the consumer's error callback; a successful
    // consume clears back to Unknown. Never Connected — see KafkaConnectionStateTracker.
    private readonly KafkaConnectionStateTracker _connectionState;
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
    // GH-4422: set when a teardown step blew through the drain budget and was abandoned on its own thread.
    // The consumer handle is still owned by that thread, so it must NOT be disposed underneath it.
    private volatile bool _teardownAbandoned;

    public KafkaListener(KafkaTopic topic, ConsumerConfig config,
        IConsumer<string, byte[]> consumer, IReceiver receiver,
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

        // GH-4522: this used to be an Information line saying reporting was simply off. It is now a Warning
        // that names the consequence -- and it should be unreachable, because Wolverine composes its tracking
        // behind a user handler rather than losing the race to register one.
        if (_connectionState.ErrorHandlerSuppressed)
        {
            _logger.LogWarning(
                "Wolverine could not install its Kafka consumer error handler for {Uri}, so it cannot observe connection errors and TransportConnectionState will report Unknown for the lifetime of the host. Health checks, wolverine-diagnostics and CritterWatch will not be able to tell a healthy consumer from one that has been disconnected for an hour. Drop the custom error handler registered through ConfigureConsumerBuilders(), or chain Wolverine's tracking from it.",
                Address);
        }
        else if (_connectionState.ComposedWithUserErrorHandler)
        {
            _logger.LogInformation(
                "Wolverine composed its Kafka connection-state tracking behind the consumer error handler registered through ConfigureConsumerBuilders for {Uri}. Both handlers run; yours runs first.",
                Address);
        }

        _messageTypeName = topic.MessageType?.ToMessageTypeName();

        Config = config;
        _receiver = receiver;
        _committer = new KafkaOffsetCommitter(consumer, config, topic.CommitMode, topic.CommitBatchCount,
            topic.CommitBatchInterval, logger);

        _consumer.Subscribe(topic.TopicName);
        // GH-4330: IConsumer.Consume(CancellationToken) blocks synchronously, so this loop holds
        // its thread for the listener's whole lifetime. On the pool that is one worker gone per
        // Kafka listener -- multiply by topics and ListenerCount and it eats into the same pool
        // the handler pipeline and every other transport's continuations run on.
        _loop = new BackgroundReceiveLoop(Address, logger, consumeOnceAsync, _cancellation.Token)
        {
            UsesBlockingIteration = true
        };
        _loop.Start();
    }

    // One consume-and-process iteration. _consumer.Consume blocks until a record (or throws). An
    // OperationCanceledException ends the loop; any other Consume error flows to BackgroundReceiveLoop's
    // log -> backoff -> continue (previously this hot-looped on every error). A processing error AFTER a
    // successful consume is a poison pill — advance past its offset and continue, exactly as before.
    //
    // GH-3490: durable endpoints additionally drain whatever records librdkafka has already
    // fetched (up to MaximumMessagesToReceive) and hand them to the receiver as one array, so
    // the durable inbox persists them with a single batched insert instead of gating the
    // consume loop on one INSERT round trip per record. Measured locally, the per-record path
    // capped a durable listener around ~1-2k msg/s while the broker backlog grew unbounded.
    private async Task<bool> consumeOnceAsync(CancellationToken token)
    {
        var result = _consumer.Consume(token);

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
            _committer.Complete(result.Topic, result.Partition.Value, result.Offset.Value);
            _logger.LogError(e, "Error trying to map Kafka message to a Wolverine envelope");
        }

        return true;
    }

    private async Task consumeSingleAsync(ConsumeResult<string, byte[]> result, CancellationToken token)
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
            _committer.Track(current.Topic, current.Partition.Value, current.Offset.Value);

            try
            {
                envelopes.Add(buildEnvelope(current));
            }
            catch (Exception e)
            {
                // Poison pill mid-batch: advance past its offset, keep the rest of the batch.
                _committer.Complete(current.Topic, current.Partition.Value, current.Offset.Value);
                _logger.LogError(e, "Error trying to map Kafka message to a Wolverine envelope");
            }

            if (envelopes.Count >= _endpoint.MaximumMessagesToReceive)
            {
                break;
            }

            // Zero-timeout poll: only drains records librdkafka already holds locally — never
            // waits on the broker, so a quiet topic still processes each record immediately.
            var next = _consumer.Consume(TimeSpan.Zero);
            if (next == null)
            {
                break;
            }

            current = next;
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
        var envelope = _endpoint.EnvelopeMapper!.CreateEnvelope(result.Topic, result.Message);
        envelope.TopicName = result.Topic;
        envelope.Offset = result.Offset.Value;
        envelope.PartitionId = result.Partition.Value;
        envelope.MessageType ??= _messageTypeName;

        if (_endpoint.GroupByMessageKey)
        {
            // GH-3140: shard by-key processing on the Kafka message key.
            envelope.GroupId = result.Message.Key;
        }
        else if (_endpoint.StampConsumerGroupIdOnEnvelope)
        {
            envelope.GroupId = Config.GroupId;
        }

        return envelope;
    }

    public ConsumerConfig Config { get; }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    // GH-3236: surface the consume loop's liveness (heartbeat + faulted/hung detection) for EndpointHealthSnapshot.
    public ReceiveLoopStatus ReceiveLoopStatus => _loop.ReceiveLoopStatus;
    public DateTimeOffset? LastReceiveLoopActivityAt => _loop.LastReceiveLoopActivityAt;

    public TransportConnectionState ConnectionState => _connectionState.ConnectionState;

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
        disposeConsumerUnlessAbandoned();
    }

    /// <summary>
    /// GH-4422. Disposing the consumer is normally safe and non-blocking -- Confluent's ReleaseHandle
    /// passes RD_KAFKA_DESTROY_F_NO_CONSUMER_CLOSE -- but only while nothing else holds the handle. When
    /// a teardown step was abandoned mid-Close, an abandoned thread is still inside that native call, so
    /// destroying the handle underneath it trades a hang for a crash. Leave it to process exit instead.
    /// </summary>
    private void disposeConsumerUnlessAbandoned()
    {
        if (_teardownAbandoned)
        {
            _logger.LogDebug(
                "Not disposing the Kafka consumer for {Uri} because a teardown step was abandoned and still owns the handle",
                Address);
            return;
        }

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

        // GH-4422: the drain above was bounded but what followed it was not, so the bound bought nothing.
        // _committer.Flush() is a synchronous _consumer.Commit() and _consumer.Close() is a synchronous
        // P/Invoke into rd_kafka_consumer_close, which waits on an infinite queue pop for the cgroup to
        // finish revoke/commit/leave. librdkafka documents the wait as "roughly limited to
        // session.timeout.ms" but against an unreachable broker or coordinator it never returns
        // (librdkafka#4519, confluent-kafka-dotnet#2013), and a host was observed still wedged 20+ minutes
        // later -- past both DrainTimeout and HostOptions.ShutdownTimeout. No cancellation token can help:
        // IListener.StopAsync has none to thread, and a managed token cannot interrupt a blocked native
        // call anyway. Bounding the wait is the only lever, so take the same trade the receivers and the
        // GCP Pub/Sub listener (#4071) already take: stop within the budget and leave the rest unsettled
        // for redelivery.
        await runTeardownWithinBudgetAsync("flush offsets and close the consumer", () =>
        {
            _committer.Flush();
            _consumer.Close();
        });
    }

    /// <summary>
    /// Runs one blocking teardown step under the drain budget. On timeout the step is ABANDONED --
    /// logged, flagged, and left running -- because a blocked native call cannot be cancelled and
    /// shutdown finishing matters more than the offsets it was trying to commit.
    /// </summary>
    private async Task runTeardownWithinBudgetAsync(string description, Action teardown)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A DEDICATED background thread, deliberately not the thread pool. If this step wedges it is
        // never coming back, and abandoning a pool thread would permanently consume a worker shared by
        // the handler pipeline and every other transport -- the same amplifier GH-4354 took out of the
        // consume loop, reintroduced at shutdown. A background thread also cannot hold up process exit.
        var thread = new Thread(() =>
        {
            try
            {
                teardown();
                completion.TrySetResult();
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
            }
        })
        {
            IsBackground = true,
            Name = "wolverine-kafka-teardown"
        };

        thread.Start();

        try
        {
            await completion.Task.WaitAsync(_drainTimeout);
        }
        catch (TimeoutException)
        {
            _teardownAbandoned = true;
            _logger.LogWarning(
                "{Uri}: the Kafka consumer did not {Description} within the drain timeout of {DrainTimeout}. Abandoning the wait so shutdown can finish; uncommitted offsets will be redelivered and the group will rebalance after session.timeout.ms.",
                Address, description, _drainTimeout);
        }
        catch (Exception e)
        {
            // Preserves the previous behaviour for a Close() that throws rather than blocks: a consumer
            // that is already closed, or a broker that refuses the commit, is not worth a louder log.
            _logger.LogDebug(e, "Error trying to {Description} for Kafka listener {Uri} on shutdown",
                description, Address);
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
            // Stamp the standard failure metadata (exception info + original
            // destination/partition/offset) so the mapper carries it as Kafka headers. GH-3474
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
            var message = await _endpoint.EnvelopeMapper!.CreateMessage(envelope);

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

        // GH-4422: _committer.Flush() is a synchronous _consumer.Commit(), which is as unbounded as
        // Close() is. This path deliberately does NOT close the consumer -- it never has -- so only the
        // flush is bounded here, leaving the rest of this method's behaviour unchanged.
        runTeardownWithinBudgetAsync("flush offsets", () => _committer.Flush()).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
        _cancellation.Dispose();
        disposeConsumerUnlessAbandoned();
    }
}
