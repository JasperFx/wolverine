using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Wolverine.Configuration;
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

        var config = new ConsumerConfig
        {
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            MaxDeliver = _endpoint.EffectiveMaxDeliveryAttempts,
            AckWait = _endpoint.JetStreamDefaults.AckWait
        };

        // Apply the per-endpoint or transport-wide DeliverPolicy override when set.
        // Leaving the property unset on the ConsumerConfig instance falls through to
        // the NATS server default of DeliverPolicy.All — which replays every existing
        // message in the stream when an auto-provisioned consumer first connects, so
        // hosts that want "only new messages from now on" need to opt in here.
        if (_endpoint.EffectiveDeliverPolicy is { } deliverPolicy)
        {
            config.DeliverPolicy = deliverPolicy;
        }

        // Scope the consumer to this listener's subject. This used to be applied only to
        // ephemeral consumers, so a durable consumer created through the fallback below
        // was provisioned with no filter at all -- and every durable consumer sharing a
        // stream then received every message published to that stream (GH-3676). The
        // AutoProvision path in NatsEndpoint has always set it for named consumers; this
        // is the runtime create-on-missing path catching up. Consumers that already exist
        // are unaffected: GetConsumerAsync succeeding means this config is never sent
        if (!string.IsNullOrEmpty(_subscriptionPattern))
        {
            // Native scheduling publishes its control message to {subject}{suffix} and no single
            // NATS filter covers both that and {subject} -- '{subject}.>' excludes {subject}
            // itself. On a work queue stream a control message no consumer covers is discarded
            // outright, so the schedule is never registered and the send silently never arrives;
            // a multi-filter consumer keeps the schedule subject owned by this endpoint
            var scheduleSubject = _subscriptionPattern + _endpoint.ScheduleSubjectSuffix;
            if (_endpoint.UsesNativeScheduledSend && !string.IsNullOrEmpty(_endpoint.ScheduleSubjectSuffix))
            {
                config.FilterSubjects = [_subscriptionPattern, scheduleSubject];
            }
            else
            {
                config.FilterSubject = _subscriptionPattern;
            }
        }

        if (!string.IsNullOrEmpty(_endpoint.ConsumerName))
        {
            config.Name = _endpoint.ConsumerName;
            config.DurableName = _endpoint.ConsumerName;

            if (!string.IsNullOrEmpty(_endpoint.EffectiveQueueGroup))
            {
                config.DeliverGroup = _endpoint.EffectiveQueueGroup;
            }

            try
            {
                _consumer = await _jetStreamContext.GetConsumerAsync(
                    _endpoint.StreamName!,
                    _endpoint.ConsumerName,
                    cancellation
                );
                _logger.LogInformation(
                    "Using existing consumer {Consumer}",
                    _endpoint.ConsumerName
                );
            }
            catch (NatsJSException)
            {
                _consumer = await _jetStreamContext.CreateOrUpdateConsumerAsync(
                    _endpoint.StreamName!,
                    config,
                    cancellation
                );
                _logger.LogInformation("Created consumer {Consumer}", _endpoint.ConsumerName);
            }
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

        _consumerTask = Task.Run(
            async () =>
            {
                await foreach (
                    var msg in _consumer!.ConsumeAsync<byte[]>(cancellationToken: cancellation)
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
        // Dispose consumer first - this will cause ConsumeAsync to complete
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
                await _consumerTask;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
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
