using Google.Cloud.PubSub.V1;
using Google.Protobuf.Collections;
using Grpc.Core;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public abstract class PubsubListener : IListener, ISupportDeadLetterQueue
{
    protected readonly CancellationTokenSource _cancellation = new();
    protected readonly RetryBlock<Envelope> _deadLetter;
    protected readonly PubsubEndpoint? _deadLetterTopic;
    protected readonly PubsubEndpoint _endpoint;
    protected readonly ILogger _logger;
    protected readonly IReceiver _receiver;
    protected readonly RetryBlock<Envelope> _requeue;
    protected readonly IWolverineRuntime _runtime;
    protected readonly PubsubTransport _transport;

    protected Task _task;
    private readonly IPubsubEnvelopeMapper _mapper;

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

        _mapper = endpoint.BuildMapper(runtime);

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

        _deadLetter = new RetryBlock<Envelope>(async (e, _) =>
        {
            if (_deadLetterTopic is null)
            {
                return;
            }
            await _deadLetterTopic.SendMessageAsync(e, _logger);
        }, _logger, runtime.Cancellation);

        _requeue = new RetryBlock<Envelope>(async (e, _) =>
        {
            await _endpoint.SendMessageAsync(e, _logger);
        }, _logger, runtime.Cancellation);

        _task = StartAsync();
    }

    public Uri Address => _endpoint.Uri;

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
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

    protected async Task<bool> handleMessageAsync(PubsubMessage message)
    {

        if (message.Attributes.Keys.Contains("batched"))
        {
            var batched = EnvelopeSerializer.ReadMany(message.Data.ToByteArray());

            if (batched.Any())
            {
                await _receiver.ReceivedAsync(this, batched);
            }

            return true;
        }

        try
        {
            var envelope = new Envelope();

            _mapper.MapIncomingToEnvelope(envelope, message);

            await _receiver.ReceivedAsync(this, [envelope]);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Uri}: Error while mapping Google Cloud Platform Pub/Sub message {MessageId}.", _endpoint.Uri, message.MessageId);
            return false;
        }
    }

}