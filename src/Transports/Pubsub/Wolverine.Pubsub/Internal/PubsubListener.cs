using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf.Collections;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util.Dataflow;

namespace Wolverine.Pubsub.Internal;

public abstract class PubsubListener2 : IListener, ISupportDeadLetterQueue {
    protected readonly RetryBlock<Envelope> _defer;
    protected readonly RetryBlock<PubsubEnvelope> _deadLetter;
    protected readonly PubsubSubscription _endpoint;
    protected readonly ILogger _logger;
    protected readonly IReceiver _receiver;
    protected readonly ISender? _deadLetterer;
    protected readonly ISender _requeuer;
    protected readonly IIncomingMapper<PubsubMessage> _mapper;
    protected readonly CancellationTokenSource _cancellation = new();

    protected RetryBlock<PubsubEnvelope[]> _complete;
    protected Task _task;

    public Uri Address => _endpoint.Uri;

    public bool NativeDeadLetterQueueEnabled { get; }

    public PubsubListener2(
        PubsubSubscription endpoint,
        ILogger logger,
        IReceiver receiver,
        ISender requeuer,
        IIncomingMapper<PubsubMessage> mapper
    ) {
        _endpoint = endpoint;
        _logger = logger;
        _receiver = receiver;
        _requeuer = requeuer;
        _mapper = mapper;

        if (_endpoint.Options.DeadLetterName.IsNotEmpty() && !_endpoint.Transport.Options.DisableDeadLetter) {
            NativeDeadLetterQueueEnabled = true;

            _deadLetterer = _endpoint.Transport.Topics[_endpoint.Options.DeadLetterName].BuildInlineSender(logger, _cancellation.Token);
        }

        _complete = new RetryBlock<PubsubEnvelope[]>(
            async (e, _) => {
                if (_endpoint.Transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

                await _endpoint.Transport.SubscriberApiClient.AcknowledgeAsync(_endpoint.SubscriptionName, e.Select(x => x.AckId));
            },
            _logger,
            _cancellation.Token
        );

        _defer = new RetryBlock<Envelope>(async (envelope, _) => {
            await _requeuer.SendAsync(envelope);
        }, _logger, _cancellation.Token);

        _deadLetter = new RetryBlock<PubsubEnvelope>(
            async (envelope, _) => {
                if (_deadLetterer is not null) await _deadLetterer.SendAsync(envelope);
            },
            _logger,
            _cancellation.Token
        );

        _task = StartAsync();
    }

    public abstract Task StartAsync();

    public virtual ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;

    public async ValueTask DeferAsync(Envelope envelope) {
        await _defer.PostAsync(envelope);
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope) {
        if (envelope is PubsubEnvelope) {
            await _defer.PostAsync(envelope);

            return true;
        }

        return false;
    }

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception) {
        if (envelope is PubsubEnvelope e) {
            await _deadLetter.PostAsync(e);
        }
    }

    public ValueTask StopAsync() {
        _cancellation.Cancel();

        return new ValueTask(_task);
    }

    public ValueTask DisposeAsync() {
        _cancellation.Cancel();
        _task.SafeDispose();
        _complete.SafeDispose();
        _defer.SafeDispose();
        _deadLetter?.SafeDispose();

        return ValueTask.CompletedTask;
    }

    protected async Task listenForMessagesAsync(Func<Task> listenAsync) {
        int retryCount = 0;

        while (!_cancellation.IsCancellationRequested) {
            try {
                await listenAsync();
            }
            catch (TaskCanceledException) when (_cancellation.IsCancellationRequested) {
                _logger.LogInformation("{Uri}: Listener canceled, shutting down listener...", _endpoint.Uri);

                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
                _logger.LogInformation("{Uri}: Listener canceled, shutting down listener...", _endpoint.Uri);

                break;
            }
            catch (Exception ex) {
                retryCount++;

                if (retryCount > _endpoint.Options.MaxRetryCount) {
                    _logger.LogError(ex, "{Uri}: Max retry attempts reached, unable to restart listener.", _endpoint.Uri);

                    break;
                }

                _logger.LogError(
                    ex,
                    "{Uri}: Error while trying to retrieve messages from Google Cloud Pub/Sub, attempting to restart stream ({RetryCount}/{MaxRetryCount})...",
                    _endpoint.Uri,
                    retryCount,
                    _endpoint.Options.MaxRetryCount
                );

                int retryDelay = (int) Math.Pow(2, retryCount) * _endpoint.Options.RetryDelay;

                await Task.Delay(retryDelay, _cancellation.Token);
            }
        }
    }

    protected async Task handleMessagesAsync(RepeatedField<ReceivedMessage> messages, Func<IEnumerable<PubsubEnvelope>, Task> acknowledgeAsync) {
        var envelopes = new List<PubsubEnvelope>(messages.Count);

        foreach (var message in messages) {
            try {
                var envelope = new PubsubEnvelope(message.Message, message.AckId);

                _mapper.MapIncomingToEnvelope(envelope, message.Message);

                if (envelope.IsPing()) {
                    try {
                        await acknowledgeAsync([envelope]);
                    }
                    catch (Exception ex) {
                        _logger.LogError(ex, "{Uri}: Error while acknowledging Google Cloud Pub/Sub ping message \"{AckId}\".", _endpoint.Uri, message.AckId);
                    }

                    continue;
                }

                envelopes.Add(envelope);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "{Uri}: Error while mapping Google Cloud Pub/Sub message {AckId}.", _endpoint.Uri, message.AckId);
            }
        }

        if (envelopes.Any()) {
            await _receiver.ReceivedAsync(this, envelopes.ToArray());
            await acknowledgeAsync(envelopes);
        }
    }
}
