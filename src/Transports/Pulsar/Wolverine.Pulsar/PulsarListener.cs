using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

internal class PulsarListener : IListener, ISupportDeadLetterQueue, ISupportRetryLetterQueue
{
    private readonly CancellationToken _cancellation;
    private readonly IConsumer<ReadOnlySequence<byte>>? _consumer;
    private readonly IConsumer<ReadOnlySequence<byte>>? _retryConsumer;
    private readonly CancellationTokenSource _localCancellation;
    private readonly Task? _receivingLoop;
    private readonly Task? _receivingRetryLoop;
    private readonly PulsarSender _sender;
    private DeadLetterPolicy? _dlqClient;

    public PulsarListener(IWolverineRuntime runtime, PulsarEndpoint endpoint, IReceiver receiver,
        PulsarTransport transport,
        CancellationToken cancellation)
    {
        if (receiver == null)
        {
            throw new ArgumentNullException(nameof(receiver));
        }

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
                                       endpoint.DeadLetterTopic is not null && endpoint.DeadLetterTopic.Mode != DeadLetterTopicMode.WolverineStorage;

        NativeRetryLetterQueueEnabled = endpoint.RetryLetterTopic is not null && RetryLetterTopic.SupportedSubscriptionTypes.Contains(endpoint.SubscriptionType);

        trySetupNativeResiliency(endpoint, transport);

        _receivingLoop = Task.Run(async () =>
        {

            await foreach (var message in _consumer.Messages(combined.Token))
            {
                var envelope = new PulsarEnvelope(message)
                {
                    Data = message.Data.ToArray()
                };
                try
                {
                    mapper.MapIncomingToEnvelope(envelope, message);

                    await receiver.ReceivedAsync(this, envelope);
                }
                catch (TaskCanceledException)
                {
                    throw; 
                }
                catch (Exception)
                {
                    if (_dlqClient != null)
                    {
                        await _dlqClient.ReconsumeLater(message);
                        await receiver.ReceivedAsync(this, envelope);
                        //await _retryConsumer.Acknowledge(message); // TODO: check: original message should be acked and copy is sent to retry topic
                    }
                }
            }


        }, combined.Token);


        if (_dlqClient != null)
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
                    try
                    {
                        mapper.MapIncomingToEnvelope(envelope, message);

                        await receiver.ReceivedAsync(this, envelope);
                    }
                    catch (TaskCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        if (_dlqClient != null)
                        {
                            // TODO: used to manage retries - refactor
                            var retryCount = int.Parse(message.Properties["RECONSUMETIMES"]);
                            await _dlqClient.ReconsumeLater(message, delayTime: endpoint.RetryLetterTopic!.Retry[retryCount]);
                            await receiver.ReceivedAsync(this, envelope);
                            //await _retryConsumer.Acknowledge(message); // TODO: check: original message should be acked and copy is sent to retry/DLQ
                        }
                    }
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

        var topicDql = NativeDeadLetterQueueEnabled ? getDeadLetteredTopicUri(endpoint) : null;
        var topicRetry = NativeRetryLetterQueueEnabled ? getRetryLetterTopicUri(endpoint) : null;
        var retryCount = NativeRetryLetterQueueEnabled ? endpoint.RetryLetterTopic!.Retry.Count : 0;

        _dlqClient = new DeadLetterPolicy(
            topicDql != null ? transport.Client!.NewProducer().Topic(topicDql.ToString()) : null,
            topicRetry != null ? transport.Client!.NewProducer().Topic(topicRetry.ToString()) : null,
            retryCount
        );
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

        if (_dlqClient != null)
        {
            await _dlqClient.DisposeAsync();
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
    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
      // TODO: pass through?
      return Task.CompletedTask;
    }

    public bool NativeRetryLetterQueueEnabled { get; }
    public Task MoveToRetryQueueAsync(Envelope envelope, Exception exception)
    {
        // TODO: how to handle retries internally?
        throw new NotImplementedException();
    }
}