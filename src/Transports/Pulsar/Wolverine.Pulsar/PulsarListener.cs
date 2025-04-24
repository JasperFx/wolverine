using System.Buffers;
using System.Globalization;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using DotPulsar.Internal;
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
    private readonly Task? _receivingRetryLoop;
    private readonly PulsarSender _sender;
    private IReceiver _receiver;
    private PulsarEndpoint _endpoint;
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

        _sender = new PulsarSender(runtime, endpoint, transport, _cancellation);
        var mapper = endpoint.BuildMapper(runtime);

        _localCancellation = new CancellationTokenSource();

        var combined = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, _localCancellation.Token);

        _consumer = transport.Client!.NewConsumer()
            .SubscriptionName(endpoint.SubscriptionName)
            .SubscriptionType(endpoint.SubscriptionType)
            .Topic(endpoint.PulsarTopic())
            .Create();

        // TODO: check
        NativeDeadLetterQueueEnabled = transport.DeadLetterTopic is not null &&
                                       transport.DeadLetterTopic.Mode != DeadLetterTopicMode.WolverineStorage ||
                                       endpoint.DeadLetterTopic is not null && endpoint.DeadLetterTopic.Mode !=
                                       DeadLetterTopicMode.WolverineStorage;

        NativeRetryLetterQueueEnabled = endpoint.RetryLetterTopic is not null &&
                                        RetryLetterTopic.SupportedSubscriptionTypes.Contains(endpoint.SubscriptionType);

        trySetupNativeResiliency(endpoint, transport);

        _receivingLoop = Task.Run(async () =>
        {

            await foreach (var message in _consumer.Messages(combined.Token))
            {
                var envelope = new PulsarEnvelope(message)
                {
                    Data = message.Data.ToArray()
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
                        Data = message.Data.ToArray()
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

        _dlqProducer = transport.Client!.NewProducer()
            .Topic(getDeadLetteredTopicUri(endpoint).ToString())
            .Create();

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
        return NativeDeadLetterQueueEnabled
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
            if (_consumer != null)
            {
                return _consumer.Acknowledge(e.MessageData, _cancellation);
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is PulsarEnvelope e)
        {
            await _consumer!.Acknowledge(e.MessageData, _cancellation);
            await _sender.SendAsync(envelope);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _localCancellation.Cancel();

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


        await _sender.DisposeAsync();

        _receivingLoop!.Dispose();
        _receivingRetryLoop?.Dispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        if (_consumer == null)
        {
            return;
        }

        await _consumer.Unsubscribe(_cancellation);
        await _consumer.RedeliverUnacknowledgedMessages(_cancellation);


        if (_retryConsumer != null)
        {
            await _retryConsumer.Unsubscribe(_cancellation);
            await _retryConsumer.RedeliverUnacknowledgedMessages(_cancellation);
        }
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (envelope is PulsarEnvelope)
        {
            await _sender.SendAsync(envelope);
            return true;
        }

        return false;
    }

    public bool NativeDeadLetterQueueEnabled { get; }
    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (NativeRetryLetterQueueEnabled && envelope is PulsarEnvelope e)
        {
            // TODO: Currently only ISupportDeadLetterQueue exists, should we introduce ISupportRetryLetterQueue concept? Because now on (first) exception, Wolverine calls this method (concept of retry letter queue is not set for Pulsar)
            await moveToQueueAsync(envelope, exception, true);
        }
    }

    public bool NativeRetryLetterQueueEnabled { get; }

    public async Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        if (NativeRetryLetterQueueEnabled && envelope is PulsarEnvelope e)
        {
            await moveToQueueAsync(e, e.Failure, false);
        }
    }


    private async Task moveToQueueAsync(Envelope envelope, Exception exception, bool isDeadLettered = false)
    {
        if (envelope is PulsarEnvelope e)
        {
            var messageMetadata = BuildMessageMetadata(envelope, e, exception, isDeadLettered);


            IConsumer<ReadOnlySequence<byte>>? associatedConsumer;
            IProducer<ReadOnlySequence<byte>> associatedProducer;

            if (NativeRetryLetterQueueEnabled && !isDeadLettered)
            {
                associatedConsumer = _retryConsumer!;
                associatedProducer = _retryLetterQueueProducer!;
            }
            else
            {
                associatedConsumer = _consumer!;
                associatedProducer = _dlqProducer!;
            }


            await associatedConsumer.Acknowledge(e.MessageData,
                _cancellation); // TODO: check: original message should be acked and copy is sent to retry/DLQ
            // TODO: check: what to do with the original message on Wolverine side? I Guess it should be acked? or we could use some kind of RequeueContinuation in FailureRuleCollection. If I understand correctly, Wolverine is/should handle original Wolverine message and its copies across Pulsar's topics as same identity?
            // TODO: e.Attempts / attempts header value  is out of sync with Pulsar's RECONSUMETIMES header!


            await associatedProducer.Send(messageMetadata, e.MessageData.Data, _cancellation)
                .ConfigureAwait(false);
        }
    }

    private MessageMetadata BuildMessageMetadata(Envelope envelope, PulsarEnvelope e, Exception exception,
        bool isDeadLettered)
    {
        var messageMetadata = new MessageMetadata();

        foreach (var property in e.Headers)
        {
            messageMetadata[property.Key] = property.Value;
        }

        //reconsumeTimesValue = GetReconsumeHeader(messageMetadata);

        if (!e.Headers.TryGetValue(PulsarEnvelopeConstants.RealTopicMetadataKey, out var originTopicNameStr))
        {
            originTopicNameStr = envelope.Headers[EnvelopeConstants.ReplyUriKey];

        }

        messageMetadata[PulsarEnvelopeConstants.RealTopicMetadataKey] = originTopicNameStr;

        var eid = e.Headers.GetValueOrDefault(PulsarEnvelopeConstants.OriginMessageIdMetadataKey, e.MessageData.MessageId.ToString());

        if (!e.Headers.ContainsKey(PulsarEnvelopeConstants.OriginMessageIdMetadataKey))
        {
            messageMetadata[PulsarEnvelopeConstants.OriginMessageIdMetadataKey] = eid;
        }

        if (NativeRetryLetterQueueEnabled)
        {

            if (!isDeadLettered)
            {
                messageMetadata[PulsarEnvelopeConstants.ReconsumeTimes] = envelope.Attempts.ToString();
                var delayTime = _endpoint.RetryLetterTopic!.Retry[envelope.Attempts - 1];
                messageMetadata[PulsarEnvelopeConstants.DelayTimeMetadataKey] = delayTime.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
                messageMetadata.DeliverAtTimeAsDateTimeOffset = DateTimeOffset.UtcNow.Add(delayTime);
            }
            else
            {
                //messageMetadata[PulsarEnvelopeConstants.DelayTimeMetadataKey] = null;
                messageMetadata.DeliverAtTimeAsDateTimeOffset = DateTimeOffset.UtcNow;
                e.Headers[PulsarEnvelopeConstants.Exception] = exception.ToString();
            }
        }


        return messageMetadata;
    }
}


public static class MessageExtensions
{
    public static bool TryGetMessageProperty(this DotPulsar.Abstractions.IMessage message, string key, out string val)
    {
        return message.Properties.TryGetValue(key , out val);
    }
}

