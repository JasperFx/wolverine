using System.Buffers;
using System.Globalization;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
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

                await receiver.ReceivedAsync(this, envelope);
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

    public RetryLetterTopic? RetryLetterTopic => _endpoint.RetryLetterTopic;

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (NativeDeadLetterQueueEnabled && envelope is PulsarEnvelope)
        {
            await moveToQueueAsync(envelope, exception, true);
        }
    }

    public bool NativeRetryLetterQueueEnabled { get; }

    public async Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        if (NativeRetryLetterQueueEnabled && envelope is PulsarEnvelope)
        {
            await moveToQueueAsync(envelope, envelope.Failure, false);
        }
    }

    private async Task moveToQueueAsync(Envelope envelope, Exception? exception, bool isDeadLettered = false)
    {
        if (envelope is PulsarEnvelope e)
        {
            var messageMetadata = BuildMessageMetadata(envelope, e, exception, isDeadLettered);

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
        bool isDeadLettered)
    {
        var messageMetadata = new MessageMetadata();

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
            messageMetadata.DeliverAtTimeAsDateTimeOffset = DateTimeOffset.UtcNow.Add(delayTime);
        }
        else
        {
            messageMetadata.DeliverAtTimeAsDateTimeOffset = DateTimeOffset.UtcNow;
            if (exception != null)
            {
                DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);

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

