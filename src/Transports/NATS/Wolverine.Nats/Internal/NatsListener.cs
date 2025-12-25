using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

public class NatsListener : IListener, ISupportDeadLetterQueue
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
                    await envelope.JetStreamMsg.NakAsync(
                        cancellationToken: _cancellation.Token
                    );
                }

                await Task.Delay(TimeSpan.FromSeconds(5), _cancellation.Token);
                await _receiver.ReceivedAsync(this, envelope);
            },
            logger,
            _cancellation.Token
        );
    }

    public Uri Address { get; }

    public bool NativeDeadLetterQueueEnabled => _subscriber.SupportsNativeDeadLetterQueue;

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is NatsEnvelope natsEnvelope)
        {
            if (NativeDeadLetterQueueEnabled && natsEnvelope.JetStreamMsg != null)
            {
                var metadata = natsEnvelope.JetStreamMsg.Metadata;

                if (metadata?.NumDelivered >= (ulong)_endpoint.MaxDeliveryAttempts)
                {
                    await natsEnvelope.JetStreamMsg.AckAsync(
                        cancellationToken: _cancellation.Token
                    );

                    if (!string.IsNullOrEmpty(_endpoint.DeadLetterSubject))
                    {
                        envelope.Attempts = (int)(metadata?.NumDelivered ?? 1);

                        envelope.Headers["x-dlq-reason"] = exception.Message;
                        envelope.Headers["x-dlq-timestamp"] = DateTimeOffset.UtcNow.ToString("O");
                        envelope.Headers["x-dlq-original-subject"] = _endpoint.Subject;
                        envelope.Headers["x-dlq-attempts"] = envelope.Attempts.ToString();
                        envelope.Headers["x-dlq-exception-type"] =
                            exception.GetType().FullName ?? "Unknown";

                        await _deadLetterSender.SendAsync(envelope);
                    }

                    _logger.LogError(
                        exception,
                        "Message {MessageId} moved to dead letter queue after {Attempts} attempts. Subject: {Subject}",
                        envelope.Id,
                        metadata?.NumDelivered ?? 1,
                        _endpoint.DeadLetterSubject
                    );
                }
            }
        }
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

    public ValueTask StopAsync()
    {
        _cancellation.Cancel();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();

        await _subscriber.DisposeAsync();

        _complete.Dispose();
        _defer.Dispose();
        _cancellation.Dispose();
    }

    internal static NatsListener Create(
        NatsEndpoint endpoint,
        NatsConnection connection,
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
            subscriber = new JetStreamSubscriber(endpoint, connection, logger, jsMapper, subscriptionPattern);
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
