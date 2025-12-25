using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
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

    public JetStreamSubscriber(
        NatsEndpoint endpoint,
        NatsConnection connection,
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
        _jetStreamContext = connection.CreateJetStreamContext();
    }

    public bool SupportsNativeDeadLetterQueue => _endpoint.DeadLetterQueueEnabled;

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
            MaxDeliver = _endpoint.MaxDeliveryAttempts,
            AckWait = TimeSpan.FromSeconds(30)
        };

        if (string.IsNullOrEmpty(_endpoint.ConsumerName))
        {
            config.FilterSubject = _subscriptionPattern;
        }

        if (!string.IsNullOrEmpty(_endpoint.ConsumerName))
        {
            config.Name = _endpoint.ConsumerName;
            config.DurableName = _endpoint.ConsumerName;

            if (!string.IsNullOrEmpty(_endpoint.QueueGroup))
            {
                config.DeliverGroup = _endpoint.QueueGroup;
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

        _consumerTask = Task.Run(
            async () =>
            {
                await foreach (
                    var msg in _consumer!.ConsumeAsync<byte[]>(cancellationToken: cancellation)
                )
                {
                    try
                    {
                        if (msg.Data == null || msg.Data.Length == 0)
                        {
                            _logger.LogDebug(
                                "Skipping empty JetStream message from subject {Subject}",
                                msg.Subject
                            );
                            await msg.AckAsync(cancellationToken: cancellation);
                            continue;
                        }

                        var envelope = new NatsEnvelope(null, msg);
                        _mapper.MapIncomingToEnvelope(envelope, msg);

                        await receiver.ReceivedAsync(listener, envelope);
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

    public async ValueTask DisposeAsync()
    {
        if (_consumer is IAsyncDisposable disposableConsumer)
        {
            await disposableConsumer.DisposeAsync();
        }

        if (_consumerTask != null)
        {
            await _consumerTask;
        }
    }
}
