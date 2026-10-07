using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Wolverine.Configuration;
using Wolverine.Nats.Configuration;
using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

internal class JetStreamSubscriber : INatsSubscriber
{
    private readonly NatsEndpoint _endpoint;
    private readonly NatsConnection _connection;
    private readonly ILogger<NatsEndpoint> _logger;
    private readonly JetStreamEnvelopeMapper _mapper;
    private readonly string _subscriptionPattern;
    private readonly INatsJSContext _jetStreamContext;
    private INatsJSConsumer? _consumer;
    private Task? _consumerTask;

    // GH-4026: the ONLY thing that ends an active ConsumeAsync enumeration is cancelling its token --
    // INatsJSConsumer is a metadata handle, and "disposing" it does not stop the pull. This is the
    // subscriber's own stop signal so disposal can end consumption while the listener's token (which
    // gates the ack RetryBlock) stays live long enough to settle everything already persisted.
    private CancellationTokenSource? _consumeCancellation;

    // GH-4026: Durable endpoints coalesce consumed messages for up to 5ms (or MaximumMessagesToReceive)
    // and hand the inbox one Envelope[] per window, the way the RabbitMQ and Kafka listeners do. Null
    // for Buffered/Inline, or when MaximumMessagesToReceive is 1.
    private BatchingChannel<Envelope>? _batching;
    private Block<Envelope[]>? _batchFlush;

    public JetStreamSubscriber(
        NatsEndpoint endpoint,
        NatsConnection connection,
        INatsJSContext jetStreamContext,
        ILogger<NatsEndpoint> logger,
        JetStreamEnvelopeMapper mapper,
        string? subscriptionPattern = null
    )
    {
        _endpoint = endpoint;
        _connection = connection;
        _logger = logger;
        _mapper = mapper;
        _subscriptionPattern = subscriptionPattern ?? endpoint.Subject;
        _jetStreamContext = jetStreamContext;
    }

    public bool SupportsNativeDeadLetterQueue => _endpoint.DeadLetterQueueEnabled;

    public TransportConnectionState ConnectionState => _connection.ConnectionState.ToTransportConnectionState();

    public async Task StartAsync(
        IListener listener,
        IReceiver receiver,
        CancellationToken cancellation
    )
    {
        _logger.LogInformation(
            "Starting JetStream listener for stream {Stream}, consumer {Consumer}, pattern {Pattern} (base subject: {Subject})",
            _endpoint.StreamName,
            _endpoint.ConsumerName ?? "(ephemeral)",
            _subscriptionPattern,
            _endpoint.Subject
        );

        // Explicit acks, AckWait, MaxDeliver, the subject filter (GH-3676, and the schedule subject under
        // native scheduled send) and -- GH-4053 -- the MaxAckPending NativeAck needs. The same set resource
        // setup writes, so the two never disagree on an existing consumer
        var config = _endpoint.ApplyManagedConsumerSettings(new ConsumerConfig(), _subscriptionPattern);

        // Apply the per-endpoint or transport-wide DeliverPolicy override when set.
        // Leaving the property unset on the ConsumerConfig instance falls through to
        // the NATS server default of DeliverPolicy.All — which replays every existing
        // message in the stream when an auto-provisioned consumer first connects, so
        // hosts that want "only new messages from now on" need to opt in here.
        if (_endpoint.EffectiveDeliverPolicy is { } deliverPolicy)
        {
            config.DeliverPolicy = deliverPolicy;
        }

        if (!string.IsNullOrEmpty(_endpoint.ConsumerName))
        {
            config.Name = _endpoint.ConsumerName;
            config.DurableName = _endpoint.ConsumerName;

            if (!string.IsNullOrEmpty(_endpoint.EffectiveQueueGroup))
            {
                config.DeliverGroup = _endpoint.EffectiveQueueGroup;
            }

            _consumer = await connectToNamedConsumerAsync(config, cancellation);
        }
        else
        {
            _consumer = await _jetStreamContext.CreateOrUpdateConsumerAsync(
                _endpoint.StreamName!,
                config,
                cancellation
            );
            _logger.LogInformation(
                "Created ephemeral consumer for subject {Subject}",
                _endpoint.Subject
            );
        }

        if (_endpoint.Mode == EndpointMode.Durable && _endpoint.MaximumMessagesToReceive > 1)
        {
            _batchFlush = new Block<Envelope[]>((batch, _) => deliverBatchAsync(batch, listener, receiver));
            _batching = new BatchingChannel<Envelope>(TimeSpan.FromMilliseconds(5), _batchFlush,
                _endpoint.MaximumMessagesToReceive);
        }

        _consumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var consumeToken = _consumeCancellation.Token;

        _consumerTask = Task.Run(
            async () =>
            {
                // GH-4026 follow-up: for a Durable endpoint, bound the client-side pull batch to
                // MaximumMessagesToReceive instead of NATS.Net's default of 1,000. Every pre-fetched
                // message counts against the consumer's MaxAckPending (server default 1,000) until it is
                // acked -- so when a back-pressure pause disposed this consumer, up to 1,000
                // buffered-but-unacked messages sat against that cap until AckWait expired, the restarted
                // listener was starved of deliveries the whole time, and then all of them came back as
                // duplicate inbox inserts. Measured on the rig as a durable listener freezing for 30s at
                // a time and ~4,000 duplicate inserts per minute. Buffered/Inline keep the client default:
                // they ack on receipt, so their in-flight window stays small without help.
                //
                // GH-4053 extends the same reasoning to NativeAck, which needs it more than Durable does: a
                // NativeAck delivery is unacked for lane queue time PLUS handler time, so a 1,000-message client
                // pull would park the whole pull window against MaxAckPending. Bounding the pull to the same
                // number of lanes MaxAckPending is sized for keeps the two consistent.
                var consumeOpts = _endpoint.Mode switch
                {
                    EndpointMode.Durable => new NatsJSConsumeOpts
                        { MaxMsgs = Math.Max(1, _endpoint.MaximumMessagesToReceive) },
                    EndpointMode.NativeAck when _endpoint.EffectiveMaxAckPending is { } pending =>
                        new NatsJSConsumeOpts { MaxMsgs = Math.Max(1, pending) },
                    _ => null
                };

                await foreach (
                    var msg in _consumer!.ConsumeAsync<byte[]>(opts: consumeOpts, cancellationToken: consumeToken)
                )
                {
                    try
                    {
                        // Skip messages without data
                        if (msg.Data == null || msg.Data.Length == 0)
                        {
                            _logger.LogDebug(
                                "Skipping empty JetStream message from subject {Subject}",
                                msg.Subject
                            );
                            await msg.AckAsync(cancellationToken: cancellation);
                            continue;
                        }

                        // Skip messages without headers or without message-type header.
                        // These are typically NATS protocol messages that should not be processed by Wolverine.
                        if (_endpoint.MessageType == null && (msg.Headers == null || !msg.Headers.ContainsKey("message-type")))
                        {
                            _logger.LogDebug(
                                "Skipping NATS message without message-type header from subject {Subject}. DataLength={DataLength}, HasHeaders={HasHeaders}",
                                msg.Subject,
                                msg.Data.Length,
                                msg.Headers != null
                            );
                            await msg.AckAsync(cancellationToken: cancellation);
                            continue;
                        }
                        var envelope = new NatsEnvelope(null, msg);
                        _mapper.MapIncomingToEnvelope(envelope, msg);

                        if (_batching != null)
                        {
                            await _batching.PostAsync(envelope);
                        }
                        else
                        {
                            await receiver.ReceivedAsync(listener, envelope);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Error processing JetStream message from subject {Subject}",
                            msg.Subject
                        );
                    }
                }
            },
            cancellation
        );
    }

    /// <summary>
    /// Create the named consumer if it is missing. An existing one is used as it is under
    /// <see cref="NatsProvisioning.CreateOnly"/>; <see cref="NatsProvisioning.CreateOrUpdate"/> -- the default
    /// for consumers -- brings the settings Wolverine manages in line first, and
    /// <see cref="NatsProvisioning.Verify"/> refuses to start on any difference.
    /// </summary>
    private async Task<INatsJSConsumer> connectToNamedConsumerAsync(ConsumerConfig config,
        CancellationToken cancellation)
    {
        var stream = _endpoint.StreamName!;
        var name = _endpoint.ConsumerName!;
        var provisioning = _endpoint.ConsumerProvisioning;

        INatsJSConsumer existing;
        try
        {
            existing = await _jetStreamContext.GetConsumerAsync(stream, name, cancellation);
        }
        catch (NatsJSApiException e) when (e.Error.Code == 404)
        {
            // Only "consumer not found" means the consumer is missing. Any other failure -- no JetStream
            // answering for the domain, missing permissions, a timeout -- propagates instead of being reported
            // as a missing consumer under Verify, or answered with a create that would have to change the
            // consumer's DeliverPolicy
            if (provisioning == NatsProvisioning.Verify)
            {
                throw new InvalidOperationException(
                    $"NATS provisioning is set to {nameof(NatsProvisioning.Verify)}, but consumer '{name}' does not exist on stream '{stream}'");
            }

            var created = await _jetStreamContext.CreateOrUpdateConsumerAsync(stream, config, cancellation);
            _logger.LogInformation("Created consumer {Consumer}", name);
            return created;
        }

        var differences = provisioning == NatsProvisioning.CreateOnly
            ? []
            : JetStreamProvisioning.CompareConsumer(config, existing.Info.Config);

        if (differences.Count == 0)
        {
            _logger.LogInformation("Using existing consumer {Consumer}", name);
            return existing;
        }

        if (provisioning == NatsProvisioning.Verify)
        {
            throw new InvalidOperationException(
                $"NATS provisioning is set to {nameof(NatsProvisioning.Verify)}, and consumer '{name}' on stream '{stream}' does not match the configuration:{Environment.NewLine}- " +
                string.Join($"{Environment.NewLine}- ", differences));
        }

        var updated = await _jetStreamContext.CreateOrUpdateConsumerAsync(stream,
            JetStreamProvisioning.OverlayManagedSettings(existing.Info.Config, config), cancellation);
        _logger.LogInformation("Updated consumer {Consumer} on stream {Stream}: {Differences}", name, stream,
            string.Join("; ", differences));
        return updated;
    }

    /// <summary>
    ///     GH-4026: one coalesced window of messages to the receiver. A failure NAKs every message in the
    ///     batch so JetStream redelivers them, exactly as the single-message path would for one.
    /// </summary>
    private async Task deliverBatchAsync(Envelope[] batch, IListener listener, IReceiver receiver)
    {
        try
        {
            await receiver.ReceivedAsync(listener, batch);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Failure receiving a batch of {Count} JetStream messages from subject {Subject}, NAKing them for redelivery",
                batch.Length, _endpoint.Subject);

            foreach (var envelope in batch.OfType<NatsEnvelope>())
            {
                try
                {
                    if (envelope.JetStreamMsg != null)
                    {
                        await envelope.JetStreamMsg.NakAsync();
                    }
                }
                catch (Exception nakException)
                {
                    _logger.LogError(nakException, "Failure NAKing JetStream message for envelope {EnvelopeId}", envelope.Id);
                }
            }
        }
    }

    public Task RepublishAsync(NatsEnvelope envelope, CancellationToken cancellation)
    {
        // JetStream uses native NAK for requeue, so this is not needed
        // This method is only called for Core NATS
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // GH-4026: cancelling the consume token is what actually ends the ConsumeAsync enumeration.
        // Disposing the consumer handle alone does not -- with the listener re-ordered to dispose the
        // subscriber before cancelling its own token, relying on the handle left the loop running
        // forever (observed as a "stopped" rig listener happily consuming another 7 million messages).
        if (_consumeCancellation != null)
        {
            try
            {
                await _consumeCancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // already torn down
            }
        }

        if (_consumer is IAsyncDisposable disposableConsumer)
        {
            try
            {
                await disposableConsumer.DisposeAsync();
            }
            catch (Exception)
            {
                // Ignore disposal errors
            }
        }

        // Wait for consumer task to complete - it should exit once consumer is disposed
        if (_consumerTask != null)
        {
            try
            {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
                // Bounded: the loop can be parked in a back-pressured PostAsync toward a receiver
                // that is being torn down; disposal must never hang on it
                await _consumerTask.WaitAsync(TimeSpan.FromSeconds(30));
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timed out waiting for the JetStream consume loop for {Subject} to stop during disposal", _endpoint.Subject);
            }
        }

        // GH-4026: whatever the consume loop posted but the 5ms window has not flushed yet still has to
        // reach the inbox (or be NAKed) before this subscriber goes away, or it sits un-ACKed until
        // AckWait and is redelivered. Bounded so a wedged receiver can never hang disposal.
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
                _logger.LogDebug(e, "Error flushing the pending JetStream receive batch for subject {Subject}", _endpoint.Subject);
            }
        }
    }
}
