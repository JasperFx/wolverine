using Amazon.SQS.Model;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsListener : IListener, ISupportDeadLetterQueue, IReportReceiveLoopHealth
{
    private readonly RetryBlock<Envelope>? _deadLetterBlock;
    private readonly AmazonSqsQueue? _deadLetterQueue;
    private readonly AmazonSqsQueue _queue;
    private readonly IReceiver _receiver;
    private readonly RetryBlock<AmazonSqsEnvelope> _requeueBlock;
    private readonly BackgroundReceiveLoop _loop;
    private readonly AmazonSqsTransport _transport;
    private readonly ISqsEnvelopeMapper _mapper;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger _logger;

    public SqsListener(IWolverineRuntime runtime, AmazonSqsQueue queue, AmazonSqsTransport transport,
        IReceiver receiver)
    {
        if (transport.Client == null)
        {
            throw new InvalidOperationException("Parent transport has not been initialized");
        }

        _mapper = queue.BuildMapper(runtime);

        _drainTimeout = runtime.DurabilitySettings.DrainTimeout;

        var logger = runtime.LoggerFactory.CreateLogger<SqsListener>();
        _logger = logger;
        _queue = queue;
        _transport = transport;
        _receiver = receiver;

        if (_queue.DeadLetterQueueName != null && !transport.DisableDeadLetterQueues)
        {
            NativeDeadLetterQueueEnabled = true;
            _deadLetterQueue = _transport.Queues[_queue.DeadLetterQueueName];
        }

        _requeueBlock = new RetryBlock<AmazonSqsEnvelope>(async (env, _) =>
        {
            if (!env.WasDeleted)
            {
                await CompleteAsync(env.SqsMessage);
            }

            await _queue.SendMessageAsync(env, logger);
        }, runtime.LoggerFactory.CreateLogger<SqsListener>(), runtime.Cancellation);

        _deadLetterBlock =
            new RetryBlock<Envelope>(async (e, _) => { await _deadLetterQueue!.SendMessageAsync(e, logger); }, logger,
                runtime.Cancellation);

        // GH-3236: the receive loop is now a shared BackgroundReceiveLoop — it owns the task, the
        // catch -> log -> exponential-backoff -> continue policy, the idle delay when a poll returns nothing, the
        // heartbeat, and safe teardown. The listener just provides one poll-and-process iteration and reports the
        // loop's health through IReportReceiveLoopHealth.
        _loop = new BackgroundReceiveLoop(_queue.Uri, logger, pollOnceAsync, runtime.Cancellation);
        _loop.Start();
    }

    private async Task<bool> pollOnceAsync(CancellationToken token)
    {
        var request = new ReceiveMessageRequest(_queue.QueueUrl);
        _queue.ConfigureRequest(request);

        var results = await _transport.Client!.ReceiveMessageAsync(request, token);

        if (results.Messages == null || !results.Messages.Any())
        {
            // No work — the loop applies its idle delay before polling again.
            return false;
        }

        var envelopes = new List<Envelope>(results.Messages.Count);
        foreach (var message in results.Messages)
        {
            try
            {
                envelopes.Add(buildEnvelope(message));
            }
            catch (Exception e)
            {
                if (_deadLetterQueue != null)
                {
                    try
                    {
                        await _transport.Client.SendMessageAsync(new SendMessageRequest(
                            _deadLetterQueue.QueueUrl,
                            message.Body));
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception,
                            "Error while trying to directly send a dead letter message {Id} from {Uri}",
                            message.MessageId, _queue.Uri);
                    }
                }

                _logger.LogError(e, "Error while reading message {Id} from {Uri}", message.MessageId, _queue.Uri);
            }
        }

        // ReSharper disable once CoVariantArrayConversion
        if (envelopes.Any())
        {
            await _receiver.ReceivedAsync(this, envelopes.ToArray());
        }

        return true;
    }

    public ReceiveLoopStatus ReceiveLoopStatus => _loop.ReceiveLoopStatus;

    public DateTimeOffset? LastReceiveLoopActivityAt => _loop.LastReceiveLoopActivityAt;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            return new ValueTask(CompleteAsync(e.SqsMessage));
        }

        return ValueTask.CompletedTask;
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            await _requeueBlock.PostAsync(e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _loop.DisposeAsync();
        _requeueBlock.Dispose();
        _deadLetterBlock?.Dispose();
    }

    public Uri Address => _queue.Uri;

    public async ValueTask StopAsync()
    {
        await _loop.StopAsync(_drainTimeout);
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            await _requeueBlock.PostAsync(e);
            return true;
        }

        return false;
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
        return _deadLetterBlock!.PostAsync(envelope);
    }

    public bool NativeDeadLetterQueueEnabled { get; }

    private AmazonSqsEnvelope buildEnvelope(Message message)
    {
        var envelope = new AmazonSqsEnvelope(message);

        // SQS only returns MessageAttributes when they were explicitly requested, and
        // brokers/SDKs may hand back a null collection when a message carries none (as is
        // the case for MassTransit/NServiceBus messages that keep their metadata in the body).
        // Guarantee a non-null dictionary so ISqsEnvelopeMapper implementations can read freely.
        var attributes = message.MessageAttributes ?? new Dictionary<string, MessageAttributeValue>();
        _mapper.ReadEnvelopeData(envelope, message.Body, attributes);

        return envelope;
    }

    public Task CompleteAsync(Message sqsMessage)
    {
        return _transport.Client!.DeleteMessageAsync(_queue.QueueUrl, sqsMessage.ReceiptHandle);
    }
}
