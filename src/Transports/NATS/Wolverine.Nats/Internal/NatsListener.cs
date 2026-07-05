using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

public class NatsListener : IListener, ISupportDeadLetterQueue, IReportConnectionState
{
    private readonly NatsEndpoint _endpoint;
    private readonly IWolverineRuntime _runtime;
    private readonly IReceiver _receiver;
    private readonly ILogger<NatsEndpoint> _logger;
    private readonly CancellationTokenSource _cancellation;
    private readonly RetryBlock<NatsEnvelope> _complete;
    private readonly RetryBlock<NatsEnvelope> _defer;
    private readonly INatsSubscriber _subscriber;
    private readonly ISender _deadLetterSender;

    public IHandlerPipeline? Pipeline { get; private set; }

    // GH-3231: surface the NATS connection state (via the subscriber that owns the connection) so external monitors
    // can detect a listener whose connection has dropped while it still reports Accepting.
    public TransportConnectionState ConnectionState => _subscriber.ConnectionState;

    internal NatsListener(
        NatsEndpoint endpoint,
        INatsSubscriber subscriber,
        IWolverineRuntime runtime,
        IReceiver receiver,
        ILogger<NatsEndpoint> logger,
        ISender deadLetterSender,
        CancellationToken parentCancellation
    )
    {
        _endpoint = endpoint;
        _subscriber = subscriber;
        _runtime = runtime;
        _receiver = receiver;
        _logger = logger;
        _deadLetterSender = deadLetterSender;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        Address = endpoint.Uri;

        _complete = new RetryBlock<NatsEnvelope>(
            async (envelope, _) =>
            {
                if (envelope.JetStreamMsg != null)
                {
                    await envelope.JetStreamMsg.AckAsync(
                        cancellationToken: _cancellation.Token
                    );
                }
            },
            logger,
            _cancellation.Token
        );

        _defer = new RetryBlock<NatsEnvelope>(
            async (envelope, _) =>
            {
                if (envelope.JetStreamMsg != null)
                {
                    // JetStream supports native NAK which will redeliver the message
                    await envelope.JetStreamMsg.NakAsync(
                        cancellationToken: _cancellation.Token
                    );
                }
                else
                {
                    // Core NATS doesn't have native requeue - republish the message to the subject
                    await _subscriber.RepublishAsync(envelope, _cancellation.Token);
                }
            },
            logger,
            _cancellation.Token
        );
    }

    public Uri Address { get; }

    public bool NativeDeadLetterQueueEnabled => _subscriber.SupportsNativeDeadLetterQueue;

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is not NatsEnvelope natsEnvelope || !NativeDeadLetterQueueEnabled ||
            natsEnvelope.JetStreamMsg == null)
        {
            return;
        }

        var metadata = natsEnvelope.JetStreamMsg.Metadata;
        if (metadata?.NumDelivered < (ulong)_endpoint.EffectiveMaxDeliveryAttempts)
        {
            return;
        }

        var attempts = (int)(metadata?.NumDelivered ?? 1);

        // Retain the poison message by forwarding a copy to the dead-letter subject BEFORE terminating,
        // so a terminate failure can't lose it. Terminating without a configured dead-letter subject drops
        // the message, so warn loudly in that case.
        if (!string.IsNullOrEmpty(_endpoint.DeadLetterSubject))
        {
            envelope.Attempts = attempts;
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
            envelope.Headers["x-dlq-original-subject"] = _endpoint.Subject;

            await _deadLetterSender.SendAsync(envelope);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Message {MessageId} exceeded {Attempts} delivery attempts on subject {Subject} but no dead-letter subject is configured; it will be terminated and dropped. Use DeadLetterTo(...) / ConfigureDeadLetterQueue(...) to retain poison messages.",
                envelope.Id,
                attempts,
                _endpoint.Subject
            );
        }

        // Terminate delivery on the JetStream consumer with a reason so the server stops redelivering and
        // records why the message was dead-lettered.
        await natsEnvelope.JetStreamMsg.AckTerminateAsync(
            $"wolverine: exceeded {attempts} delivery attempts ({exception.GetType().Name})",
            cancellationToken: _cancellation.Token
        );

        _logger.LogError(
            exception,
            "Message {MessageId} terminated after {Attempts} delivery attempts. Subject: {Subject}, DeadLetter: {DeadLetter}",
            envelope.Id,
            attempts,
            _endpoint.Subject,
            _endpoint.DeadLetterSubject ?? "(none)"
        );
    }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is NatsEnvelope natsEnvelope)
        {
            await _complete.PostAsync(natsEnvelope);
        }
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is NatsEnvelope natsEnvelope)
        {
            await _defer.PostAsync(natsEnvelope);
        }
    }

    public async Task StartAsync()
    {
        await _subscriber.StartAsync(this, _receiver, _cancellation.Token);
    }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        await _subscriber.DisposeAsync();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cancellation.IsCancellationRequested)
        {
            await _cancellation.CancelAsync();
            await _subscriber.DisposeAsync();
        }

        _complete.Dispose();
        _defer.Dispose();
        _cancellation.Dispose();
    }

    internal static NatsListener Create(
        NatsEndpoint endpoint,
        NatsConnection connection,
        INatsJSContext? jetStreamContext,
        IWolverineRuntime runtime,
        IReceiver receiver,
        ILogger<NatsEndpoint> logger,
        ISender? deadLetterSender,
        CancellationToken cancellation,
        bool useJetStream,
        string? subscriptionPattern = null,
        ITenantSubjectMapper? tenantMapper = null
    )
    {
        INatsSubscriber subscriber;
        if (useJetStream)
        {
            var jsMapper = new JetStreamEnvelopeMapper(endpoint, tenantMapper);
            if (endpoint.MessageType != null)
            {
                jsMapper.ReceivesMessage(endpoint.MessageType);
            }
            subscriber = new JetStreamSubscriber(endpoint, connection, jetStreamContext!, logger, jsMapper, subscriptionPattern);
        }
        else
        {
            var mapper = new NatsEnvelopeMapper(endpoint, tenantMapper);
            if (endpoint.MessageType != null)
            {
                mapper.ReceivesMessage(endpoint.MessageType);
            }
            subscriber = new CoreNatsSubscriber(endpoint, connection, logger, mapper, subscriptionPattern);
        }

        return new NatsListener(
            endpoint,
            subscriber,
            runtime,
            receiver,
            logger,
            deadLetterSender ?? new NullSender(),
            cancellation
        );
    }
}
