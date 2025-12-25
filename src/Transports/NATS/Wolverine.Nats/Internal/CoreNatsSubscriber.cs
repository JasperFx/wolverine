using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

internal class CoreNatsSubscriber : INatsSubscriber
{
    private readonly NatsEndpoint _endpoint;
    private readonly NatsConnection _connection;
    private readonly ILogger<NatsEndpoint> _logger;
    private readonly NatsEnvelopeMapper _mapper;
    private readonly string _subscriptionPattern;
    private readonly List<IAsyncDisposable> _subscriptions = new();
    private readonly List<Task> _consumerTasks = new();

    public CoreNatsSubscriber(
        NatsEndpoint endpoint,
        NatsConnection connection,
        ILogger<NatsEndpoint> logger,
        NatsEnvelopeMapper mapper,
        string? subscriptionPattern = null
    )
    {
        _endpoint = endpoint;
        _connection = connection;
        _logger = logger;
        _mapper = mapper;
        _subscriptionPattern = subscriptionPattern ?? endpoint.Subject;
    }

    public bool SupportsNativeDeadLetterQueue => false;

    public async Task StartAsync(
        IListener listener,
        IReceiver receiver,
        CancellationToken cancellation
    )
    {
        var patterns = new List<string>();

        if (_subscriptionPattern != _endpoint.Subject && _subscriptionPattern.StartsWith("*."))
        {
            patterns.Add(_subscriptionPattern);
            patterns.Add(_endpoint.Subject);

            _logger.LogInformation(
                "Multi-tenant subscription: listening to patterns '{WildcardPattern}' and '{BaseSubject}' for subject '{Subject}'",
                _subscriptionPattern,
                _endpoint.Subject,
                _endpoint.Subject
            );
        }
        else
        {
            patterns.Add(_subscriptionPattern);

            _logger.LogInformation(
                "Starting Core NATS listener for pattern {Pattern} (base subject: {Subject}) with queue group {QueueGroup}",
                _subscriptionPattern,
                _endpoint.Subject,
                _endpoint.QueueGroup ?? "(none)"
            );
        }

        foreach (var pattern in patterns)
        {
            IAsyncDisposable subscription;

            if (!string.IsNullOrEmpty(_endpoint.QueueGroup))
            {
                subscription = await _connection.SubscribeCoreAsync<byte[]>(
                    pattern,
                    _endpoint.QueueGroup,
                    cancellationToken: cancellation
                );
            }
            else
            {
                subscription = await _connection.SubscribeCoreAsync<byte[]>(
                    pattern,
                    cancellationToken: cancellation
                );
            }

            _subscriptions.Add(subscription);

            var consumerTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await foreach (
                            var msg in ((INatsSub<byte[]>)subscription).Msgs.ReadAllAsync(cancellation)
                        )
                        {
                            try
                            {
                                if (msg.Data == null || msg.Data.Length == 0)
                                {
                                    _logger.LogDebug(
                                        "Skipping empty NATS message from subject {Subject}",
                                        msg.Subject
                                    );
                                    continue;
                                }

                                var envelope = new NatsEnvelope(msg, null);
                                _mapper.MapIncomingToEnvelope(envelope, msg);

                                await receiver.ReceivedAsync(listener, envelope);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(
                                    ex,
                                    "Error processing NATS message from subject {Subject}",
                                    msg.Subject
                                );
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug(
                            "NATS listener for pattern {Pattern} was cancelled",
                            pattern
                        );
                    }
                },
                cancellation
            );

            _consumerTasks.Add(consumerTask);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync();
        }

        if (_consumerTasks.Any())
        {
            await Task.WhenAll(_consumerTasks);
        }
    }
}
