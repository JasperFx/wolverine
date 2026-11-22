using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Pulsar.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

internal class PulsarListener : IListener, ISupportDeadLetterQueue, ISupportNativeScheduling, IReportConnectionState
{
    // GH-4100: how long a failed receiving loop waits before re-entering Messages(). Long enough that a
    // permanently faulted consumer logs at a readable rate, short enough that a transient fault costs
    // roughly nothing.
    private static readonly TimeSpan ReceivingLoopRestartDelay = TimeSpan.FromSeconds(1);

    private readonly CancellationToken _cancellation;
    private readonly IConsumer<ReadOnlySequence<byte>>? _consumer;
    private readonly IConsumer<ReadOnlySequence<byte>>? _retryConsumer;
    private readonly CancellationTokenSource _localCancellation;
    private readonly CancellationTokenSource _combinedCancellation;
    private readonly Task? _receivingLoop;
    private readonly PulsarSender? _sender;
    private readonly bool _enableRequeue;
    private readonly bool _unsubscribeOnClose;
    private readonly IReceiver _receiver;
    private readonly Task? _receivingRetryLoop;
    private readonly PulsarEndpoint _endpoint;
    private readonly PulsarAckHandler _ackHandler;
    private readonly IWolverineRuntime _runtime;
    private readonly PulsarTransport _transport;

    // GH-5768: a listener over several topics requeues a message to the topic it came from, through one producer
    // per topic, created the first time a message from that topic is requeued. The primary topic uses _sender.
    private readonly bool _listensToSeveralTopics;
    private readonly ConcurrentDictionary<string, Lazy<PulsarSender>> _topicSenders = new();

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

    // Gives a message without an id of its own the id of its MessageId, so the durable inbox sees a redelivery of
    // it as the duplicate it is. Null for an endpoint without an inbox to deduplicate against.
    private readonly PulsarMessageIdentity? _messageIdentity;

    // GH-3231: DotPulsar exposes consumer state only via change-notifications, so a background monitor tracks the
    // latest state here. Volatile because it is written by the monitor task and read by external health probes.
    private volatile TransportConnectionState _connectionState = TransportConnectionState.Disconnected;

    public TransportConnectionState ConnectionState => _connectionState;

    public PulsarListener(IWolverineRuntime runtime, PulsarEndpoint endpoint, IReceiver receiver,
        PulsarTransport transport,
        CancellationToken cancellation, string? tenantId = null)
    {
        _endpoint = endpoint;
        _runtime = runtime;
        _transport = transport;
        _listensToSeveralTopics = endpoint.TopicsPattern is not null || endpoint.AdditionalTopics.Count > 0;
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

        // Only the durable inbox deduplicates on the envelope id. Anywhere else a derived id would be a hash per
        // message on the hot path that buys nothing. The endpoint's scheme is the broker name, the same for the
        // default listener and every broker-per-tenant listener, which the tenant id tells apart.
        _messageIdentity = endpoint.Mode == EndpointMode.Durable
            ? new PulsarMessageIdentity(endpoint.Uri.Scheme, tenantId, endpoint.SubscriptionName)
            : null;

        _localCancellation = new CancellationTokenSource();

        _combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, _localCancellation.Token);

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

        _receivingLoop = Task.Run(
            () => consumeAsync(_consumer, _combinedCancellation.Token, async message =>
            {
                // Record receipt so the ack handler can compute the cumulative-ack watermark safely.
                _ackHandler.Track(FixMessageId(message.MessageId, _consumer.Topic));

                var envelope = PulsarMessageIdentity.CreateEnvelope(mapper, message, _consumer.Topic,
                    isFromRetryConsumer: false, _messageIdentity);

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
            }), _combinedCancellation.Token);


        if (NativeRetryLetterQueueEnabled)
        {
            var retryConsumer = createRetryConsumer(endpoint, transport);
            _retryConsumer = retryConsumer;
            _receivingRetryLoop = Task.Run(
                () => consumeAsync(retryConsumer, _combinedCancellation.Token, async message =>
                {
                    // A retry copy is a new message on the retry letter topic, so one without an id property gets
                    // the id of its own MessageId there. See PulsarMessageIdentity.
                    var envelope = PulsarMessageIdentity.CreateEnvelope(mapper, message,
                        retryConsumer.Topic, isFromRetryConsumer: true, _messageIdentity);

                    if (_codec != null)
                    {
                        envelope.Message = _codec.Decode(message.Data);
                    }

                    await receiver.ReceivedAsync(this, envelope);
                }), _combinedCancellation.Token);
        }
    }

    /// <summary>
    ///     GH-4100. The receiving loop for one consumer, with the two guards it has to have.
    ///
    ///     <para>Both Pulsar receiving loops used to be a bare <c>await foreach</c> over
    ///     <c>IConsumer.Messages()</c> inside a fire-and-forget <see cref="Task.Run(Func{Task})" />,
    ///     with no <c>try</c> anywhere in them and nothing ever observing the returned task. Anything
    ///     that threw — the envelope mapper, a schema codec, the receiver, or DotPulsar itself faulting
    ///     the consumer — killed that task for good. The consumer object stayed alive, the socket to the
    ///     broker stayed open, and <see cref="ConnectionState" /> (which is fed by DotPulsar's state
    ///     handler, not by this loop) went on reporting Connected, so the listener read no further
    ///     messages and said nothing about it. That is the silently-dead listener the RabbitMQ transport
    ///     fixed in #3391, and it is the shape of the CIPulsar wedge in #4100: a fully built host, a live
    ///     connection, and total silence until the test's tracked-session budget ran out.</para>
    ///
    ///     <para>So: an inner guard per message, and an outer guard around the enumeration.</para>
    ///
    ///     <para>The per-message failure is logged and skipped, and the message is deliberately left
    ///     <em>unacknowledged</em> rather than deferred. Deferring is what the batched path does, but a
    ///     mapper or codec failure is deterministic — deferring it would redeliver the same message into
    ///     the same exception forever at whatever rate the broker allows. Leaving it unacked neither
    ///     drops it silently nor spins on it.</para>
    ///
    ///     <para>The enumeration failure is logged and the loop re-enters <c>Messages()</c> after a short
    ///     pause. DotPulsar reconnects the consumer underneath us, so a transient broker fault costs a
    ///     restart instead of the listener. A consumer that is permanently faulted re-throws immediately,
    ///     which the pause turns into one log line per second — noisy on purpose, and still far better
    ///     than the silence it replaces.</para>
    /// </summary>
    private async Task consumeAsync(IConsumer<ReadOnlySequence<byte>> consumer, CancellationToken token,
        Func<IMessage<ReadOnlySequence<byte>>, Task> handleAsync)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in consumer.Messages(token))
                {
                    try
                    {
                        await handleAsync(message);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e,
                            "Failure receiving Pulsar message {MessageId} on topic {Topic} at {Address}; the message is left unacknowledged and the listener continues",
                            message.MessageId, consumer.Topic, Address);
                    }
                }

                // A clean end of the stream means the consumer was closed underneath us; there is
                // nothing left to re-enumerate.
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogError(e,
                    "The Pulsar receiving loop for topic {Topic} at {Address} failed; restarting it",
                    consumer.Topic, Address);

                try
                {
                    await Task.Delay(ReceivingLoopRestartDelay, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
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

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is PulsarEnvelope e)
        {
            await ackOriginalAsync(e);
        }
    }

    /// <summary>
    ///     Settle the original delivery. Retry-consumer messages always acknowledge individually; the configurable
    ///     ack strategy (#3180) applies to the primary consumer.
    ///
    ///     <para>GH-5768: a requeue or a retry/dead-letter move settles the original through here too. Acking it
    ///     directly on the consumer left its id in the ack handler's in-flight set, and under
    ///     <see cref="PulsarAckStrategy.Cumulative" /> the watermark never moved past it again.</para>
    /// </summary>
    private async ValueTask ackOriginalAsync(PulsarEnvelope e)
    {
        // GH-5813: a Buffered listener's ack block can reach a delivery after the failed handler has already asked
        // Pulsar to redeliver it natively. Acking it now would cancel that redelivery, or ack the redelivered copy.
        if (!e.TryClaimAck())
        {
            _logger.LogDebug(
                "Pulsar message {EnvelopeId} at {Address} is being redelivered natively, so it is not acknowledged",
                e.Id, Address);
            return;
        }

        if (e.IsFromRetryConsumer && _retryConsumer != null)
        {
            await _retryConsumer.Acknowledge(FixMessageId(e.MessageData.MessageId, _retryConsumer.Topic),
                _cancellation);
        }
        else if (_consumer != null)
        {
            await _ackHandler.CompleteAsync(FixMessageId(e.MessageData.MessageId, _consumer.Topic));
        }

        e.OriginalAcked = true;
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is not PulsarEnvelope e)
        {
            return;
        }

        // GH-5768: Pulsar can only redeliver a message it still holds unacknowledged. A Buffered listener has
        // already acked it, so a native redelivery there did nothing and the message was lost; it gets a copy instead.
        // GH-5813: the claim also covers an ack that is still on its way to the broker.
        if (_endpoint.UseNativeRedelivery && e.TryClaimNativeRedelivery())
        {
            await redeliverNativelyAsync(e);
            return;
        }

        if (_enableRequeue && _sender is not null)
        {
            // GH-5768: the copy FIRST, and the original is acked only once the broker has accepted it. Acking first
            // lost the message whenever the send failed or the process exited in between.
            await sendRequeueCopyAsync(e);

            if (!e.OriginalAcked)
            {
                await ackOriginalAsync(e);
            }

            return;
        }

        if (e.OriginalAcked || _endpoint.UseNativeRedelivery)
        {
            _logger.LogWarning(
                "Pulsar message {EnvelopeId} at {Address} was already acknowledged and cannot be redelivered natively, and requeue is disabled for this endpoint, so it is not requeued",
                envelope.Id, Address);
        }
    }

    private async ValueTask redeliverNativelyAsync(PulsarEnvelope e)
    {
        var consumer = e.IsFromRetryConsumer && _retryConsumer != null ? _retryConsumer : _consumer;
        // Native per-message redelivery: leave the message unacknowledged and ask Pulsar to
        // redeliver just this one message (preserves its redelivery count) — #3177.
        await consumer!.RedeliverUnacknowledgedMessages(
            [FixMessageId(e.MessageData.MessageId, consumer.Topic)], _cancellation);
    }

    /// <summary>
    ///     GH-5768. Send the requeue copy unless an earlier attempt of this requeue already did. The flag is set
    ///     before the original is acked, so a retry after a failed ack does not send a second copy.
    /// </summary>
    private async ValueTask sendRequeueCopyAsync(PulsarEnvelope e)
    {
        if (e.CopySent)
        {
            return;
        }

        await requeueSenderFor(e).SendCopyAsync(e, e.MessageData);
        e.CopySent = true;
    }

    /// <summary>
    ///     GH-5768. The topic a requeued message goes back to: the one it came from. A listener over one topic
    ///     always uses the primary topic.
    /// </summary>
    private PulsarSender requeueSenderFor(PulsarEnvelope e)
    {
        if (!_listensToSeveralTopics)
        {
            return _sender!;
        }

        var topic = sourceTopicOf(e);
        if (topic is null || topic == _endpoint.PulsarTopic())
        {
            return _sender!;
        }

        return _topicSenders.GetOrAdd(topic,
            t => new Lazy<PulsarSender>(() => new PulsarSender(_runtime, _endpoint, _transport, _cancellation, t)))
            .Value;
    }

    /// <summary>
    ///     GH-5813. The topic a message of a multi-topic listener was first consumed from. A message from the main
    ///     consumer says so on its message id. A message from the retry-letter consumer carries it in the
    ///     <see cref="PulsarEnvelopeConstants.SourceTopicMetadataKey" /> property its retry-letter move stamped, and
    ///     that is used only when it names a topic this listener consumes. Null for the primary topic.
    /// </summary>
    private string? sourceTopicOf(PulsarEnvelope e)
    {
        if (!e.IsFromRetryConsumer)
        {
            return SourceTopicOf(e.MessageData.MessageId);
        }

        return e.Headers.TryGetValue(PulsarEnvelopeConstants.SourceTopicMetadataKey, out var topic) &&
               topic is { Length: > 0 } && consumesTopic(topic)
            ? topic
            : null;
    }

    private bool consumesTopic(string topic)
    {
        if (_endpoint.TopicsPattern is { } pattern)
        {
            var match = pattern.Match(topic);
            return match.Success && match.Index == 0 && match.Length == topic.Length;
        }

        return _endpoint.AllTopicPaths().Contains(topic);
    }

    /// <summary>
    ///     GH-5768. The topic a message was consumed from, as a multi-topic consumer reports it on the message id,
    ///     with the partition suffix of a partitioned topic taken off so the copy goes through the topic's own
    ///     routing. Null when the id does not say.
    /// </summary>
    internal static string? SourceTopicOf(MessageId messageId)
    {
        var topic = messageId.Topic;
        if (string.IsNullOrEmpty(topic))
        {
            return null;
        }

        const string partitionMarker = "-partition-";
        var index = topic.LastIndexOf(partitionMarker, StringComparison.Ordinal);
        if (index > 0)
        {
            var suffix = topic.AsSpan(index + partitionMarker.Length);
            if (suffix.Length > 0 && int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return topic[..index];
            }
        }

        return topic;
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

        foreach (var topicSender in _topicSenders.Values)
        {
            if (topicSender.IsValueCreated)
            {
                await topicSender.Value.DisposeAsync();
            }
        }

        // GH-4100: Task.Dispose() throws InvalidOperationException on a task that has not reached a
        // final state, and cancelling the token above does not by itself mean the receiving loops have
        // unwound yet. Wait for them (bounded, so a wedged receiver can never hang disposal) before
        // disposing, and swallow the cancellation they finish with.
        foreach (var loop in new[] { _receivingLoop, _receivingRetryLoop })
        {
            if (loop == null)
            {
                continue;
            }

            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error waiting on the Pulsar receiving loop at {Address} during disposal",
                    Address);
            }

            if (loop.IsCompleted)
            {
                loop.Dispose();
            }
        }

        _combinedCancellation.Dispose();
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

    /// <summary>
    /// GH-5533 test seam: ask Pulsar to redeliver everything this consumer holds unacknowledged, exactly as
    /// <see cref="StopAsync"/> does on the way out, but without stopping -- which is how a test puts a
    /// redelivery in front of the receiver while the original delivery is still parked in a handler.
    /// </summary>
    internal async Task RedeliverUnacknowledgedMessagesAsync()
    {
        if (_consumer != null)
        {
            await _consumer.RedeliverUnacknowledgedMessages(_cancellation);
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
            // GH-5768: only the Buffered receiver calls this, after it has already acked the message. Native
            // redelivery of an acked message does nothing, so it only applies while the message is still unacked.
            // GH-5813: the receiver acks from a separate block, so the handler can fail before that ack. Whichever
            // claims the delivery first wins: a native redelivery that wins keeps the ack from running, and an ack
            // that wins, even one still in flight, gets a copy here instead.
            if (_endpoint.UseNativeRedelivery && e.TryClaimNativeRedelivery())
            {
                await redeliverNativelyAsync(e);
                return true;
            }

            if (_sender is not null)
            {
                await sendRequeueCopyAsync(e);
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

            // For retry, the message comes from the main consumer on the first retry and from the retry consumer
            // after that; either way the original is acked on whichever consumer it came from
            var targetProducer = isDeadLettered ? _dlqProducer! : _retryLetterQueueProducer!;

            // GH-5768: send the copy to the retry/DLQ topic FIRST and ack the original only once the broker has
            // accepted it. Acking first lost the message whenever the send failed or the process exited in between.
            // CopySent keeps a retry after a failed ack from sending a second copy.
            if (!e.CopySent)
            {
                await targetProducer.Send(messageMetadata, e.MessageData.Data, _cancellation)
                    .ConfigureAwait(false);
                e.CopySent = true;
            }

            if (!e.OriginalAcked)
            {
                await ackOriginalAsync(e);
            }
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

        // GH-5768: the copy keeps the key it was received with
        PulsarSender.CopyKeys(e.MessageData, messageMetadata);

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

        // GH-5813: a listener over several topics stamps the topic a message was consumed from on its retry-letter
        // copy, so a requeue of the retried message goes back to that topic. A message from the retry-letter
        // consumer already carries it in its headers, copied above.
        if (_listensToSeveralTopics && !e.IsFromRetryConsumer && SourceTopicOf(e.MessageData.MessageId) is { } sourceTopic)
        {
            messageMetadata[PulsarEnvelopeConstants.SourceTopicMetadataKey] = sourceTopic;
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

