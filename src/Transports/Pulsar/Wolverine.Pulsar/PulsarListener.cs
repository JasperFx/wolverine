using System.Buffers;
using System.Globalization;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

internal class PulsarListener : IListener, ISupportDeadLetterQueue, ISupportNativeScheduling, IReportConnectionState
{
    private readonly CancellationToken _cancellation;
    private readonly IConsumer<ReadOnlySequence<byte>>? _consumer;
    private readonly IConsumer<ReadOnlySequence<byte>>? _retryConsumer;
    private readonly CancellationTokenSource _localCancellation;
    private readonly Task? _receivingLoop;
    private readonly PulsarSender? _sender;
    private readonly bool _enableRequeue;
    private readonly bool _unsubscribeOnClose;
    private readonly IReceiver _receiver;
    private readonly Task? _receivingRetryLoop;
    private readonly PulsarEndpoint _endpoint;
    private readonly PulsarAckHandler _ackHandler;

    // GH-4026: a Durable endpoint coalesces consumed messages for up to 5ms (or MaximumMessagesToReceive)
    // and hands the inbox one Envelope[] per window, the way the RabbitMQ, Kafka and NATS listeners do.
    // Null for Buffered/Inline, or when MaximumMessagesToReceive is 1. The retry-letter consumer stays
    // one at a time.
    private readonly BatchingChannel<Envelope>? _batching;
    private readonly Block<Envelope[]>? _batchFlush;
    private readonly ILogger _logger;
    private readonly Schemas.IPulsarMessageCodec? _codec;
    private IProducer<ReadOnlySequence<byte>>? _retryLetterQueueProducer;
    private IProducer<ReadOnlySequence<byte>>? _dlqProducer;

    // GH-3231: DotPulsar exposes consumer state only via change-notifications, so a background monitor tracks the
    // latest state here. Volatile because it is written by the monitor task and read by external health probes.
    private volatile TransportConnectionState _connectionState = TransportConnectionState.Disconnected;

    public TransportConnectionState ConnectionState => _connectionState;

    public PulsarListener(IWolverineRuntime runtime, PulsarEndpoint endpoint, IReceiver receiver,
        PulsarTransport transport,
        CancellationToken cancellation)
    {
        _endpoint = endpoint;
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _cancellation = cancellation;
        _codec = endpoint.MessageCodec;
        _logger = runtime.LoggerFactory.CreateLogger<PulsarListener>();

        // GH-4047. Belt and braces with the bootstrap check in PulsarEndpoint.validateModeConfiguration(): that one
        // runs over every *listening* endpoint at startup, this one covers any path that builds a listener without
        // going through it. A cumulative ack under NativeAck is silent message loss, so it must be impossible to
        // reach a running consumer in that state, not merely unlikely.
        if (endpoint.Mode == EndpointMode.NativeAck && endpoint.AckStrategy == PulsarAckStrategy.Cumulative)
        {
            throw new InvalidOperationException(
                PulsarEndpoint.CumulativeAckIsIncompatibleWithNativeAck(endpoint));
        }

        if (endpoint.AckStrategy == PulsarAckStrategy.Cumulative &&
            endpoint.SubscriptionType is SubscriptionType.Shared or SubscriptionType.KeyShared)
        {
            throw new InvalidOperationException(
                $"Cumulative acknowledgment is only valid for Exclusive or Failover subscriptions, not " +
                $"{endpoint.SubscriptionType}, on Pulsar topic {endpoint.PulsarTopic()}. Use Individual or " +
                "Batched acknowledgment for shared subscriptions.");
        }

        Address = endpoint.Uri;

        _enableRequeue = endpoint.EnableRequeue;

        if (_enableRequeue)
        {
            _sender = new PulsarSender(runtime, endpoint, transport, _cancellation);
        }

        _unsubscribeOnClose = endpoint.UnsubscribeOnClose;

        var mapper = endpoint.BuildMapper(runtime);

        _localCancellation = new CancellationTokenSource();

        var combined = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, _localCancellation.Token);

        // GH-3183: when an endpoint schema is configured, create the consumer with it so the broker
        // registers/enforces the schema for the topic. The schema is a pass-through over Wolverine's
        // bytes, so the builder is still IConsumerBuilder<ReadOnlySequence<byte>> and the receive path is
        // unchanged.
        var consumerBuilder = (endpoint.Schema != null
                ? transport.Client!.NewConsumer(endpoint.Schema)
                : transport.Client!.NewConsumer())
            .SubscriptionName(endpoint.SubscriptionName)
            .SubscriptionType(endpoint.SubscriptionType)
            .InitialPosition(endpoint.SubscriptionInitialPosition)
            // GH-3231: track the consumer's connection state so health probes can read it (see ConnectionState). A
            // user-supplied StateChangedHandler via ConfigureConsumer below would override this.
            .StateChangedHandler(changed => _connectionState = changed.ConsumerState.ToTransportConnectionState());

        if (endpoint.TopicsPattern is not null)
        {
            // Pattern subscription: consume every topic matching the regex.
            consumerBuilder = consumerBuilder
                .TopicsPattern(endpoint.TopicsPattern)
                .RegexSubscriptionMode(endpoint.RegexSubscriptionMode);
        }
        else if (endpoint.AdditionalTopics.Count > 0)
        {
            // Multi-topic subscription: one consumer over the primary + additional topics.
            consumerBuilder = consumerBuilder.Topics(endpoint.AllTopicPaths());
        }
        else
        {
            consumerBuilder = consumerBuilder.Topic(endpoint.PulsarTopic());
        }

        // GH-4047. Pulsar's client-side receiver queue is the prefetch analogue. Only set when the endpoint asks for
        // one -- explicitly, or by being in NativeAck mode, whose default sizes the queue to the lanes that can be
        // busy at once instead of DotPulsar's 1000. Applied BEFORE the user hook so ConfigureConsumer still wins.
        if (endpoint.EffectiveReceiverQueueSize is { } prefetch)
        {
            consumerBuilder = consumerBuilder.MessagePrefetchCount(prefetch);
        }

        endpoint.ConfigureConsumer?.Invoke(consumerBuilder);

        _consumer = consumerBuilder.Create();

        _ackHandler = new PulsarAckHandler(_consumer, endpoint.AckStrategy, endpoint.AckBatchSize,
            endpoint.AckBatchInterval, _cancellation);

        // Per-endpoint-override-wins resolution (endpoint override, else transport default).
        NativeDeadLetterQueueEnabled = endpoint.EffectiveDeadLetterTopic is not null &&
                                       endpoint.EffectiveDeadLetterTopic.Mode != DeadLetterTopicMode.WolverineStorage;

        NativeRetryLetterQueueEnabled = endpoint.EffectiveRetryLetterTopic is not null &&
                                        RetryLetterTopic.SupportedSubscriptionTypes.Contains(endpoint.SubscriptionType);

        trySetupNativeResiliency(endpoint, transport);

        if (endpoint.Mode == EndpointMode.Durable && endpoint.MaximumMessagesToReceive > 1)
        {
            _batchFlush = new Block<Envelope[]>((batch, _) => deliverBatchAsync(batch));
            _batching = new BatchingChannel<Envelope>(TimeSpan.FromMilliseconds(5), _batchFlush,
                endpoint.MaximumMessagesToReceive);
        }

        _receivingLoop = Task.Run(async () =>
        {
            await foreach (var message in _consumer.Messages(combined.Token))
            {
                // Record receipt so the ack handler can compute the cumulative-ack watermark safely.
                _ackHandler.Track(FixMessageId(message.MessageId, _consumer.Topic));

                var envelope = new PulsarEnvelope(message)
                {
                    Data = message.Data.ToArray(),
                    IsFromRetryConsumer = false
                };

                mapper.MapIncomingToEnvelope(envelope, message);

                // GH-3213: a schema codec (Avro) owns the body encoding, so decode the message object
                // directly here — the pipeline then skips its own body deserialization.
                if (_codec != null)
                {
                    envelope.Message = _codec.Decode(message.Data);
                }

                if (_batching != null)
                {
                    await _batching.PostAsync(envelope);
                }
                else
                {
                    await receiver.ReceivedAsync(this, envelope);
                }
            }
        }, combined.Token);


        if (NativeRetryLetterQueueEnabled)
        {
            _retryConsumer = createRetryConsumer(endpoint, transport);
            _receivingRetryLoop = Task.Run(async () =>
            {
                await foreach (var message in _retryConsumer.Messages(combined.Token))
                {
                    var envelope = new PulsarEnvelope(message)
                    {
                        Data = message.Data.ToArray(),
                        IsFromRetryConsumer = true
                    };

                    mapper.MapIncomingToEnvelope(envelope, message);

                    if (_codec != null)
                    {
                        envelope.Message = _codec.Decode(message.Data);
                    }

                    await receiver.ReceivedAsync(this, envelope);
                }
            }, combined.Token);
        }
    }

    /// <summary>
    ///     GH-4149. Block until this listener's consumers have actually subscribed at the broker.
    ///
    ///     <para>DotPulsar's <c>IConsumerBuilder.Create()</c> returns as soon as the consumer object
    ///     exists; the Subscribe command travels to the broker on a background task. Wolverine's listener
    ///     startup did not wait for it, so <c>IHost.StartAsync()</c> returned with the listener reporting
    ///     started while the topic did not yet exist at the broker — measured directly: the admin API
    ///     answered <c>404</c> for the topic at the instant start returned, on five runs out of five.</para>
    ///
    ///     <para>Because <see cref="PulsarEndpoint.SubscriptionInitialPosition" /> defaults to
    ///     <see cref="SubscriptionInitialPosition.Latest" />, anything published into that window is not
    ///     delivered to the subscription at all. It is silently dropped: no error, no redelivery, and on a
    ///     brand-new topic no earlier position to fall back to. That is a real message-loss window on
    ///     first deployment of a service that publishes to a topic it also listens to, and it is what makes
    ///     the Pulsar suite drop exactly the first message it publishes under parallel load.</para>
    ///
    ///     <para>A consumer leaves <see cref="ConsumerState.Disconnected" /> once the broker has
    ///     acknowledged the subscribe, so that transition is the signal. Waiting for <em>any</em> state
    ///     other than Disconnected rather than for Active specifically matters for Failover subscriptions,
    ///     where a standby consumer is legitimately established but Inactive.</para>
    ///
    ///     <para>Bounded, and a timeout is logged rather than thrown: a broker that is slow or briefly
    ///     unreachable at startup must not stop the host from coming up. The listener still works once
    ///     DotPulsar connects — the wait closes the ordering race, it does not add a hard dependency.</para>
    /// </summary>
    internal async Task WaitForSubscriptionAsync(TimeSpan timeout)
    {
        await waitForConsumerAsync(_consumer, timeout);
        await waitForConsumerAsync(_retryConsumer, timeout);
    }

    private async Task waitForConsumerAsync(IConsumer<ReadOnlySequence<byte>>? consumer, TimeSpan timeout)
    {
        if (consumer == null)
        {
            return;
        }

        // NOTE: deliberately the no-delay overload. DotPulsar's StateChangedFrom(state, TimeSpan, ct)
        // takes a *settle* delay, not a timeout -- it waits the full TimeSpan and only then reports the
        // state, so using it here added the whole budget to every single host start (measured: 10,030ms
        // per start against a broker that had gone Active in milliseconds). The timeout has to be ours.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await consumer.StateChangedFrom(ConsumerState.Disconnected, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Shutting down before the subscription was ever established; nothing to report.
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "The Pulsar consumer for topic {Topic} at {Address} had not subscribed after {Timeout}; starting the listener anyway. Messages published to this topic before the subscription is established may not be delivered to it.",
                consumer.Topic, Address, timeout);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Error waiting for the Pulsar subscription on topic {Topic} at {Address} to be established; starting the listener anyway",
                consumer.Topic, Address);
        }
    }

    private void trySetupNativeResiliency(PulsarEndpoint endpoint, PulsarTransport transport)
    {
        if (!NativeRetryLetterQueueEnabled && !NativeDeadLetterQueueEnabled)
        {
            return;
        }

        if (endpoint.EffectiveRetryLetterTopic is not null)
        {
            _retryLetterQueueProducer = transport.Client!.NewProducer()
                .Topic(getRetryLetterTopicUri(endpoint)!.ToString())
                .Create();
        }

        if (NativeDeadLetterQueueEnabled)
        {
            _dlqProducer = transport.Client!.NewProducer()
                .Topic(getDeadLetteredTopicUri(endpoint).ToString())
                .Create();
        }
    }

    private IConsumer<ReadOnlySequence<byte>> createRetryConsumer(PulsarEndpoint endpoint, PulsarTransport transport)
    {
        var topicRetry = getRetryLetterTopicUri(endpoint);

        var retryBuilder = transport.Client!.NewConsumer()
            .SubscriptionName(endpoint.SubscriptionName)
            .SubscriptionType(endpoint.SubscriptionType)
            .InitialPosition(endpoint.SubscriptionInitialPosition)
            .Topic(topicRetry!.ToString());

        // GH-4047. The retry-letter consumer holds unacked deliveries on exactly the same terms as the primary one,
        // so it gets the same receiver queue sizing.
        if (endpoint.EffectiveReceiverQueueSize is { } prefetch)
        {
            retryBuilder = retryBuilder.MessagePrefetchCount(prefetch);
        }

        endpoint.ConfigureConsumer?.Invoke(retryBuilder);

        return retryBuilder.Create();
    }

    private Uri? getRetryLetterTopicUri(PulsarEndpoint endpoint)
    {
        return NativeRetryLetterQueueEnabled
            ? PulsarEndpoint.NativeTopicPath(endpoint.IsPersistent, endpoint.Tenant, endpoint.Namespace,
                endpoint.EffectiveRetryLetterTopic?.TopicName ?? $"{endpoint.TopicName}-RETRY")
            : null;
    }

    private Uri getDeadLetteredTopicUri(PulsarEndpoint endpoint)
    {
        return PulsarEndpoint.NativeTopicPath(endpoint.IsPersistent, endpoint.Tenant, endpoint.Namespace,
            endpoint.EffectiveDeadLetterTopic?.TopicName ?? $"{endpoint.TopicName}-DLQ");
    }

    /// <summary>
    ///     GH-4026: one coalesced window of messages to the receiver. A failure defers every message in
    ///     the batch -- native redelivery or ack-and-requeue, whichever this endpoint is configured for --
    ///     exactly as the single-message path would for one.
    /// </summary>
    private async Task deliverBatchAsync(Envelope[] batch)
    {
        try
        {
            await _receiver.ReceivedAsync(this, batch);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Failure receiving a batch of {Count} Pulsar messages at {Address}, deferring them for redelivery",
                batch.Length, Address);

            foreach (var envelope in batch)
            {
                try
                {
                    await DeferAsync(envelope);
                }
                catch (Exception deferException)
                {
                    _logger.LogError(deferException, "Failure deferring Pulsar message for envelope {EnvelopeId}", envelope.Id);
                }
            }
        }
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is PulsarEnvelope e)
        {
            // Retry-consumer messages always acknowledge individually; the configurable ack strategy
            // (#3180) applies to the primary consumer.
            if (e.IsFromRetryConsumer && _retryConsumer != null)
            {
                return _retryConsumer.Acknowledge(FixMessageId(e.MessageData.MessageId, _retryConsumer.Topic),
                    _cancellation);
            }

            if (_consumer != null)
            {
                return _ackHandler.CompleteAsync(FixMessageId(e.MessageData.MessageId, _consumer.Topic));
            }
        }

        return ValueTask.CompletedTask;
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is not PulsarEnvelope e)
        {
            return;
        }

        if (_endpoint.UseNativeRedelivery)
        {
            var consumer = e.IsFromRetryConsumer && _retryConsumer != null ? _retryConsumer : _consumer;
            // Native per-message redelivery: leave the message unacknowledged and ask Pulsar to
            // redeliver just this one message (preserves its redelivery count) — #3177.
            await consumer!.RedeliverUnacknowledgedMessages(
                [FixMessageId(e.MessageData.MessageId, consumer.Topic)], _cancellation);
            return;
        }

        if (_enableRequeue && _sender is not null)
        {
            var consumer = e.IsFromRetryConsumer && _retryConsumer != null ? _retryConsumer : _consumer;
            await consumer!.Acknowledge(FixMessageId(e.MessageData.MessageId, consumer.Topic), _cancellation);
            await _sender.SendAsync(envelope);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _localCancellation.CancelAsync();
        _localCancellation.Dispose();

        // GH-4026: whatever the receive loop posted but the 5ms window has not flushed yet still has to
        // reach the inbox (or be deferred) before the consumer is torn down. Bounded so a wedged receiver
        // can never hang disposal.
        if (_batching != null)
        {
            try
            {
                _batching.TriggerBatch();
                _batching.Complete();
                await _batching.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error flushing the pending Pulsar receive batch at {Address}", Address);
            }
        }

        // Flush any pending batched acks before the consumer is torn down.
        await _ackHandler.DisposeAsync();

        if (_consumer != null)
        {
            await _consumer.DisposeAsync();
        }

        if (_retryConsumer != null)
        {
            await _retryConsumer.DisposeAsync();
        }

        if (_retryLetterQueueProducer != null)
        {
            await _retryLetterQueueProducer.DisposeAsync();
        }

        if (_dlqProducer != null)
        {
            await _dlqProducer.DisposeAsync();
        }

        if (_sender != null)
        {
            await _sender.DisposeAsync();
        }

        _receivingLoop?.Dispose();
        _receivingRetryLoop?.Dispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        if (_consumer == null)
        {
            return;
        }

        if (_unsubscribeOnClose)
        {
            await _consumer.Unsubscribe(_cancellation);
        }

        await _consumer.RedeliverUnacknowledgedMessages(_cancellation);

        if (_retryConsumer != null)
        {
            if (_unsubscribeOnClose)
            {
                await _retryConsumer.Unsubscribe(_cancellation);
            }
            await _retryConsumer.RedeliverUnacknowledgedMessages(_cancellation);
        }
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (!_enableRequeue)
        {
            throw new InvalidOperationException("Requeue is not enabled for this endpoint");
        }

        if (envelope is PulsarEnvelope e)
        {
            if (_endpoint.UseNativeRedelivery)
            {
                var consumer = e.IsFromRetryConsumer && _retryConsumer != null ? _retryConsumer : _consumer;
                await consumer!.RedeliverUnacknowledgedMessages(
                    [FixMessageId(e.MessageData.MessageId, consumer.Topic)], _cancellation);
                return true;
            }

            if (_sender is not null)
            {
                await _sender.SendAsync(envelope);
                return true;
            }
        }

        return false;
    }

    public bool NativeDeadLetterQueueEnabled { get; }

    /// <summary>
    /// Whether this listener should use Pulsar's native per-message redelivery for failed messages
    /// when no retry-letter / dead-letter topic is configured. See #3177.
    /// </summary>
    internal bool UsesNativeRedelivery => _endpoint.UseNativeRedelivery;

    /// <summary>
    /// Is there any native Pulsar resiliency for this listener to hand a failure to? When there is not,
    /// <see cref="ErrorHandling.PulsarNativeContinuationSource" /> declines the failure so that Wolverine's
    /// own error policies get it instead of the message being silently dropped.
    /// </summary>
    internal bool HasNativeResiliency =>
        NativeRetryLetterQueueEnabled || NativeDeadLetterQueueEnabled || UsesNativeRedelivery;

    public RetryLetterTopic? RetryLetterTopic => _endpoint.RetryLetterTopic;

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (NativeDeadLetterQueueEnabled && envelope is PulsarEnvelope)
        {
            await moveToQueueAsync(envelope, exception, true);
        }
    }

    public bool NativeRetryLetterQueueEnabled { get; }

    // Only claim the native-scheduling capability when the retry-letter queue is actually configured;
    // otherwise MoveToScheduledUntilAsync has no producer to move the message to and the runtime should
    // fall back to the durable/buffered rescheduler rather than believe a native reschedule succeeded.
    public bool NativeSchedulingEnabled => NativeRetryLetterQueueEnabled;

    public async Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        if (NativeRetryLetterQueueEnabled && envelope is PulsarEnvelope)
        {
            // Honor the caller's requested delivery time rather than the retry-letter tier schedule.
            await moveToQueueAsync(envelope, envelope.Failure, false, time);
        }
    }

    private async Task moveToQueueAsync(Envelope envelope, Exception? exception, bool isDeadLettered = false,
        DateTimeOffset? scheduledTime = null)
    {
        if (envelope is PulsarEnvelope e)
        {
            var messageMetadata = BuildMessageMetadata(envelope, e, exception, isDeadLettered, scheduledTime);

            IConsumer<ReadOnlySequence<byte>>? sourceConsumer;
            IProducer<ReadOnlySequence<byte>> targetProducer;

            if (isDeadLettered)
            {
                // For DLQ, acknowledge on whichever consumer the message came from
                sourceConsumer = e.IsFromRetryConsumer && _retryConsumer != null ? _retryConsumer : _consumer;
                targetProducer = _dlqProducer!;
            }
            else
            {
                // For retry, message always comes from main consumer on first retry,
                // then from retry consumer on subsequent retries
                sourceConsumer = e.IsFromRetryConsumer && _retryConsumer != null ? _retryConsumer : _consumer;
                targetProducer = _retryLetterQueueProducer!;
            }

            // Acknowledge the original message
            await sourceConsumer!.Acknowledge(FixMessageId(e.MessageData.MessageId, sourceConsumer.Topic), _cancellation);

            // Send copy to retry/DLQ topic
            await targetProducer.Send(messageMetadata, e.MessageData.Data, _cancellation)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Workaround for https://github.com/apache/pulsar-dotpulsar/issues/287. DotPulsar's
    /// <c>BatchHandler.Add</c> constructs the inner <see cref="MessageId" /> for each message of a
    /// batch via the four-arg ctor and never sets <see cref="MessageId.Topic" />, so
    /// <c>Consumer.Acknowledge</c> hits <c>_subConsumers[messageId.Topic]</c> with the empty
    /// string and throws <see cref="System.Collections.Generic.KeyNotFoundException" /> on
    /// partitioned topics. Reconstruct the <see cref="MessageId" /> with the partition sub-topic
    /// URI built from the <see cref="IConsumer.Topic" /> the source consumer was subscribed to.
    /// Using <c>consumer.Topic</c> rather than <c>_endpoint.PulsarTopic()</c> means the same
    /// helper works for the retry consumer (which subscribes to <c>{baseTopic}-RETRY</c>) without
    /// any branching — DotPulsar reports the actual subscribed topic per consumer.
    /// </summary>
    internal static MessageId FixMessageId(MessageId messageId, string consumerTopic)
    {
        if (string.IsNullOrEmpty(messageId.Topic) && messageId.Partition >= 0)
        {
            return new MessageId(messageId.LedgerId, messageId.EntryId, messageId.Partition,
                messageId.BatchIndex, $"{consumerTopic}-partition-{messageId.Partition}");
        }

        return messageId;
    }

    private MessageMetadata BuildMessageMetadata(Envelope envelope, PulsarEnvelope e, Exception? exception,
        bool isDeadLettered, DateTimeOffset? scheduledTime = null)
    {
        var messageMetadata = new MessageMetadata();

        // Stamp the standard failure metadata (exception info + original destination) BEFORE
        // copying the headers into the outgoing metadata so it actually reaches the DLQ or
        // retry-letter message on the wire. GH-3474
        if (exception != null)
        {
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
        }

        foreach (var property in e.Headers)
        {
            messageMetadata[property.Key] = property.Value;
        }

        if (!e.Headers.TryGetValue(PulsarEnvelopeConstants.RealTopicMetadataKey, out var originTopicNameStr))
        {
            e.Headers.TryGetValue(EnvelopeConstants.ReplyUriKey, out originTopicNameStr);
        }

        if (originTopicNameStr != null)
        {
            messageMetadata[PulsarEnvelopeConstants.RealTopicMetadataKey] = originTopicNameStr;
        }

        var eid = e.Headers.GetValueOrDefault(PulsarEnvelopeConstants.OriginMessageIdMetadataKey,
            e.MessageData.MessageId.ToString());

        if (!e.Headers.ContainsKey(PulsarEnvelopeConstants.OriginMessageIdMetadataKey))
        {
            messageMetadata[PulsarEnvelopeConstants.OriginMessageIdMetadataKey] = eid;
        }

        if (!isDeadLettered)
        {
            messageMetadata[PulsarEnvelopeConstants.ReconsumeTimes] = envelope.Attempts.ToString();
            var delayTime = _endpoint.RetryLetterTopic!.Retry[envelope.Attempts - 1];
            messageMetadata[PulsarEnvelopeConstants.DelayTimeMetadataKey] =
                delayTime.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
            // A caller-supplied reschedule time (ReScheduleAsync / scheduled-retry policy) wins over the
            // retry-letter tier delay so "retry at T" is actually honored; fall back to the tier schedule.
            messageMetadata.DeliverAtTimeAsDateTimeOffset = scheduledTime ?? DateTimeOffset.UtcNow.Add(delayTime);
        }
        else
        {
            messageMetadata.DeliverAtTimeAsDateTimeOffset = DateTimeOffset.UtcNow;
            if (exception != null)
            {
                var exceptionText = exception.ToString();
                messageMetadata[PulsarEnvelopeConstants.Exception] = exceptionText;
                e.Headers[PulsarEnvelopeConstants.Exception] = exceptionText;
            }
        }

        return messageMetadata;
    }
}


public static class MessageExtensions
{
    public static bool TryGetMessageProperty(this DotPulsar.Abstractions.IMessage message, string key, out string? val)
    {
        return message.Properties.TryGetValue(key, out val);
    }
}

