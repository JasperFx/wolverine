using Google.Cloud.PubSub.V1;
using Google.Protobuf.Collections;
using Grpc.Core;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.Pubsub.Internal;

public abstract class PubsubListener : IListener, ISupportDeadLetterQueue
{
    protected readonly CancellationTokenSource _cancellation = new();
    protected readonly RetryBlock<Envelope[]> _complete;
    protected readonly RetryBlock<Envelope> _deadLetter;
    protected readonly PubsubEndpoint? _deadLetterTopic;
    protected readonly PubsubEndpoint _endpoint;
    protected readonly ILogger _logger;
    protected readonly IReceiver _receiver;
    protected readonly RetryBlock<Envelope> _requeue;
    protected readonly IWolverineRuntime _runtime;
    protected readonly PubsubTransport _transport;

    protected RetryBlock<string[]> _acknowledge;
    protected Task _task;

    public PubsubListener(
        PubsubEndpoint endpoint,
        PubsubTransport transport,
        IReceiver receiver,
        IWolverineRuntime runtime
    )
    {
        if (transport.SubscriberApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        _endpoint = endpoint;
        _transport = transport;
        _receiver = receiver;
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<PubsubListener>();

        if (_endpoint.DeadLetterName.IsNotEmpty())
        {
            _deadLetterTopic = _transport.Topics[_endpoint.DeadLetterName];

            NativeDeadLetterQueueEnabled = true;
        }

        _acknowledge = new RetryBlock<string[]>(async (ackIds, _) =>
        {
            if (transport.SubscriberApiClient is null)
            {
                throw new WolverinePubsubTransportNotConnectedException();
            }

            if (ackIds.Any())
            {
                await transport.SubscriberApiClient.AcknowledgeAsync(
                    _endpoint.Server.Subscription.Name,
                    ackIds
                );
            }
        }, _logger, runtime.Cancellation);

        _deadLetter = new RetryBlock<Envelope>(async (e, _) =>
        {
            if (_deadLetterTopic is null)
            {
                return;
            }

            if (e is PubsubEnvelope pubsubEnvelope)
            {
                await _acknowledge.PostAsync([pubsubEnvelope.AckId]);
            }

            await _deadLetterTopic.SendMessageAsync(e, _logger);
        }, _logger, runtime.Cancellation);

        _requeue = new RetryBlock<Envelope>(async (e, _) =>
        {
            if (e is PubsubEnvelope pubsubEnvelope)
            {
                await _acknowledge.PostAsync([pubsubEnvelope.AckId]);
            }

            await _endpoint.SendMessageAsync(e, _logger);
        }, _logger, runtime.Cancellation);

        _complete = new RetryBlock<Envelope[]>(async (envelopes, _) =>
        {
            var pubsubEnvelopes = envelopes.OfType<PubsubEnvelope>().ToArray();

            if (!pubsubEnvelopes.Any())
            {
                return;
            }

            if (transport.SubscriberApiClient is null)
            {
                throw new WolverinePubsubTransportNotConnectedException();
            }

            var ackIds = pubsubEnvelopes
                .Select(e => e.AckId)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct()
                .ToArray();

            await _acknowledge.PostAsync(ackIds);
        }, _logger, _cancellation.Token);

        _task = StartAsync();
    }

    public Uri Address => _endpoint.Uri;

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        await _complete.PostAsync([envelope]);
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        await _requeue.PostAsync(envelope);
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        await _requeue.PostAsync(envelope);

        return true;
    }

    public ValueTask StopAsync()
    {
        _cancellation.Cancel();

        return new ValueTask(_task);
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
        _complete.SafeDispose();
        _requeue.SafeDispose();
        _deadLetter.SafeDispose();

        return ValueTask.CompletedTask;
    }

    public bool NativeDeadLetterQueueEnabled { get; }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        return _deadLetter.PostAsync(envelope);
    }

    public abstract Task StartAsync();

    protected async Task listenForMessagesAsync(Func<Task> listenAsync)
    {
        var retryCount = 0;

        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await listenAsync();
            }
            catch (TaskCanceledException) when (_cancellation.IsCancellationRequested)
            {
                _logger.LogInformation("{Uri}: Listener canceled, shutting down listener...", _endpoint.Uri);

                break;
            }
            // This is a know issue at the moment:
            // https://github.com/googleapis/google-cloud-java/issues/4220
            // https://stackoverflow.com/questions/60012138/google-cloud-function-pulling-from-pub-sub-subscription-throws-exception-deadl
            catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
            {
                _logger.LogError(ex,
                    "{Uri}: Google Cloud Platform Pub/Sub returned \"DEADLINE_EXCEEDED\", attempting to restart listener.",
                    _endpoint.Uri);

                _task.SafeDispose();
                _task = StartAsync();

                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                _logger.LogInformation("{Uri}: Listener canceled, shutting down listener...", _endpoint.Uri);

                break;
            }
            catch (Exception ex)
            {
                retryCount++;

                if (retryCount > _endpoint.Client.RetryPolicy.MaxRetryCount)
                {
                    _logger.LogError(ex, "{Uri}: Max retry attempts reached, unable to restart listener.",
                        _endpoint.Uri);

                    throw;
                }

                _logger.LogError(
                    ex,
                    "{Uri}: Error while trying to retrieve messages from Google Cloud Platform Pub/Sub, attempting to restart listener ({RetryCount}/{MaxRetryCount})...",
                    _endpoint.Uri,
                    retryCount,
                    _endpoint.Client.RetryPolicy.MaxRetryCount
                );

                var retryDelay = (int)Math.Pow(2, retryCount) * _endpoint.Client.RetryPolicy.RetryDelay;

                await Task.Delay(retryDelay, _cancellation.Token);
            }
        }
    }

    protected async Task handleMessagesAsync(RepeatedField<ReceivedMessage> messages)
    {
        var envelopes = new List<PubsubEnvelope>(messages.Count);

        foreach (var message in messages)
        {
            if (message.Message.Attributes.Keys.Contains("batched"))
            {
                var batched = EnvelopeSerializer.ReadMany(message.Message.Data.ToByteArray());

                if (batched.Any())
                {
                    await _receiver.ReceivedAsync(this, batched);
                }

                await _acknowledge.PostAsync([message.AckId]);

                continue;
            }

            try
            {
                var envelope = new PubsubEnvelope();

                _endpoint.Mapper.MapIncomingToEnvelope(envelope, message);

                if (envelope.IsPing())
                {
                    try
                    {
                        await _complete.PostAsync([envelope]);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "{Uri}: Error while acknowledging Google Cloud Platform Pub/Sub ping message \"{AckId}\".",
                            _endpoint.Uri, message.AckId);
                    }

                    continue;
                }

                envelopes.Add(envelope);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Uri}: Error while mapping Google Cloud Platform Pub/Sub message {AckId}.",
                    _endpoint.Uri, message.AckId);
            }
        }

        if (envelopes.Any())
        {
            await _receiver.ReceivedAsync(this, envelopes.ToArray());
            await _complete.PostAsync(envelopes.ToArray());
        }
    }
}