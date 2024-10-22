using Google.Cloud.PubSub.V1;
using Google.Protobuf.Collections;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.Pubsub.Internal;

public abstract class PubsubListener : IListener, ISupportDeadLetterQueue {
    protected readonly PubsubEndpoint _endpoint;
    protected readonly PubsubTransport _transport;
    protected readonly IReceiver _receiver;
    protected readonly ILogger _logger;
    protected readonly PubsubEndpoint? _deadLetterTopic;
    protected readonly RetryBlock<Envelope> _resend;
    protected readonly RetryBlock<Envelope> _deadLetter;
    protected readonly CancellationTokenSource _cancellation = new();

    protected RetryBlock<Envelope[]> _complete;
    protected Task _task;

    public bool NativeDeadLetterQueueEnabled { get; } = false;
    public Uri Address => _endpoint.Uri;

    public PubsubListener(
        PubsubEndpoint endpoint,
        PubsubTransport transport,
        IReceiver receiver,
        IWolverineRuntime runtime
    ) {
        if (transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        _endpoint = endpoint;
        _transport = transport;
        _receiver = receiver;
        _logger = runtime.LoggerFactory.CreateLogger<PubsubListener>();

        _resend = new(async (envelope, _) => {
            await _endpoint.SendMessageAsync(envelope, _logger);
        }, _logger, runtime.Cancellation);

        if (_endpoint.DeadLetterName.IsNotEmpty() && transport.EnableDeadLettering) {
            NativeDeadLetterQueueEnabled = true;
            _deadLetterTopic = _transport.Topics[_endpoint.DeadLetterName];
        }

        _deadLetter = new(async (e, _) => {
            if (_deadLetterTopic is null) return;

            await _deadLetterTopic.SendMessageAsync(e, _logger);
        }, _logger, runtime.Cancellation);

        _complete = new(async (e, _) => {
            if (transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

            await transport.SubscriberApiClient.AcknowledgeAsync(
                _endpoint.Server.Subscription.Name,
                e.Select(x => x.Headers[PubsubTransport.AckIdHeader])
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct()
            );
        }, _logger, _cancellation.Token);

        _task = StartAsync();
    }

    public abstract Task StartAsync();

    public virtual ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;

    public async ValueTask DeferAsync(Envelope envelope) {
        if (envelope.Headers.ContainsKey(PubsubTransport.AckIdHeader)) await _resend.PostAsync(envelope);
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception) => _deadLetter.PostAsync(envelope) ?? Task.CompletedTask;

    public async Task<bool> TryRequeueAsync(Envelope envelope) {
        if (envelope.Headers.ContainsKey(PubsubTransport.AckIdHeader)) {
            await _resend.PostAsync(envelope);

            return true;
        }

        return false;
    }

    public ValueTask StopAsync() {
        _cancellation.Cancel();

        return new ValueTask(_task);
    }

    public ValueTask DisposeAsync() {
        _cancellation.Cancel();
        _task.SafeDispose();
        _complete.SafeDispose();
        _resend.SafeDispose();
        _deadLetter.SafeDispose();

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

                if (retryCount > _endpoint.Client.RetryPolicy.MaxRetryCount) {
                    _logger.LogError(ex, "{Uri}: Max retry attempts reached, unable to restart listener.", _endpoint.Uri);

                    throw;
                }

                _logger.LogError(
                    ex,
                    "{Uri}: Error while trying to retrieve messages from Google Cloud Pub/Sub, attempting to restart stream ({RetryCount}/{MaxRetryCount})...",
                    _endpoint.Uri,
                    retryCount,
                    _endpoint.Client.RetryPolicy.MaxRetryCount
                );

                int retryDelay = (int) Math.Pow(2, retryCount) * _endpoint.Client.RetryPolicy.RetryDelay;

                await Task.Delay(retryDelay, _cancellation.Token);
            }
        }
    }

    protected async Task handleMessagesAsync(RepeatedField<ReceivedMessage> messages) {
        var envelopes = new List<Envelope>(messages.Count);

        foreach (var message in messages) {
            try {
                var envelope = EnvelopeSerializer.Deserialize(message.Message.Data.ToByteArray());

                _endpoint.Mapper.MapIncomingToEnvelope(envelope, message.Message);

                envelope.Headers.Add(PubsubTransport.AckIdHeader, message.AckId);

                if (envelope.IsPing()) {
                    try {
                        await _complete.PostAsync([envelope]);
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
            await _complete.PostAsync(envelopes.ToArray());
        }
    }
}
