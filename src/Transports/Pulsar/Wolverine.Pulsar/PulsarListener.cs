using System.Buffers;
using System.Globalization;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

internal class PulsarListener : IListener, ISupportDeadLetterQueue, ISupportNativeScheduling
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
    private IProducer<ReadOnlySequence<byte>>? _retryLetterQueueProducer;
    private IProducer<ReadOnlySequence<byte>>? _dlqProducer;

    public PulsarListener(IWolverineRuntime runtime, PulsarEndpoint endpoint, IReceiver receiver,
        PulsarTransport transport,
        CancellationToken cancellation)
    {
        _endpoint = endpoint;
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _cancellation = cancellation;

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

        _consumer = transport.Client!.NewConsumer()
            .SubscriptionName(endpoint.SubscriptionName)
            .SubscriptionType(endpoint.SubscriptionType)
            .Topic(endpoint.PulsarTopic())
            .Create();

        NativeDeadLetterQueueEnabled = (transport.DeadLetterTopic is not null &&
                                       transport.DeadLetterTopic.Mode != DeadLetterTopicMode.WolverineStorage) ||
                                       (endpoint.DeadLetterTopic is not null && endpoint.DeadLetterTopic.Mode !=
                                       DeadLetterTopicMode.WolverineStorage);

        NativeRetryLetterQueueEnabled = endpoint.RetryLetterTopic is not null &&
                                        RetryLetterTopic.SupportedSubscriptionTypes.Contains(endpoint.SubscriptionType);

        trySetupNativeResiliency(endpoint, transport);

        _receivingLoop = Task.Run(async () =>
        {
            await foreach (var message in _consumer.Messages(combined.Token))
            {
                var envelope = new PulsarEnvelope(message)
                {
                    Data = message.Data.ToArray(),
                    IsFromRetryConsumer = false
                };

                mapper.MapIncomingToEnvelope(envelope, message);

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

        if (endpoint.RetryLetterTopic is not null)
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

        return transport.Client!.NewConsumer()
            .SubscriptionName(endpoint.SubscriptionName)
            .SubscriptionType(endpoint.SubscriptionType)
            .Topic(topicRetry!.ToString())
            .Create();
    }

    private Uri? getRetryLetterTopicUri(PulsarEndpoint endpoint)
    {
        return NativeRetryLetterQueueEnabled
            ? PulsarEndpoint.UriFor(endpoint.IsPersistent, endpoint.Tenant, endpoint.Namespace,
                endpoint.RetryLetterTopic?.TopicName ?? $"{endpoint.TopicName}-RETRY")
            : null;
    }

    private Uri getDeadLetteredTopicUri(PulsarEndpoint endpoint)
    {
        var topicDql = PulsarEndpoint.UriFor(endpoint.IsPersistent, endpoint.Tenant, endpoint.Namespace,
            endpoint.DeadLetterTopic?.TopicName ?? $"{endpoint.TopicName}-DLQ");

        return topicDql;
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is PulsarEnvelope e)
        {
            // Acknowledge on the appropriate consumer based on where the message came from
            var consumer = e.IsFromRetryConsumer && _retryConsumer != null ? _retryConsumer : _consumer;
            if (consumer != null)
            {
                return consumer.Acknowledge(e.MessageData, _cancellation);
            }
        }

        return ValueTask.CompletedTask;
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (_enableRequeue && _sender is not null && envelope is PulsarEnvelope e)
        {
            var consumer = e.IsFromRetryConsumer && _retryConsumer != null ? _retryConsumer : _consumer;
            await consumer!.Acknowledge(e.MessageData, _cancellation);
            await _sender.SendAsync(envelope);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _localCancellation.CancelAsync();

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

        if (_sender is not null && envelope is PulsarEnvelope)
        {
            await _sender.SendAsync(envelope);
            return true;
        }

        return false;
    }

    public bool NativeDeadLetterQueueEnabled { get; }
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
            await sourceConsumer!.Acknowledge(e.MessageData, _cancellation);

            // Send copy to retry/DLQ topic
            await targetProducer.Send(messageMetadata, e.MessageData.Data, _cancellation)
                .ConfigureAwait(false);
        }
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

