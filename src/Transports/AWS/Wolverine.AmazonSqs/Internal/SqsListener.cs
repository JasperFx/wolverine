using Amazon.SQS.Model;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsListener : IListener, ISupportDeadLetterQueue
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RetryBlock<Envelope>? _deadLetterBlock;
    private readonly AmazonSqsQueue? _deadLetterQueue;
    private readonly AmazonSqsQueue _queue;
    private readonly IReceiver _receiver;
    private readonly RetryBlock<AmazonSqsEnvelope> _requeueBlock;
    private readonly Task _task;
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

        var failedCount = 0;

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

        _receiver = receiver;

        _task = Task.Run(async () =>
        {
            while (!_cancellation.Token.IsCancellationRequested)
            {
                try
                {
                    var request = new ReceiveMessageRequest(_queue.QueueUrl);

                    _queue.ConfigureRequest(request);

                    var results = await _transport.Client.ReceiveMessageAsync(request, _cancellation.Token);

                    failedCount = 0;

                    if (results.Messages != null && results.Messages.Any())
                    {
                        var envelopes = new List<Envelope>(results.Messages.Count);
                        foreach (var message in results.Messages)
                        {
                            try
                            {
                                var envelope = buildEnvelope(message);

                                envelopes.Add(envelope);
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
                                        logger.LogError(exception,
                                            "Error while trying to directly send a dead letter message {Id} from {Uri}",
                                            message.MessageId, _queue.Uri);
                                    }
                                }

                                logger.LogError(e, "Error while reading message {Id} from {Uri}", message.MessageId,
                                    _queue.Uri);
                            }
                        }

                        // ReSharper disable once CoVariantArrayConversion
                        if (envelopes.Any())
                        {
                            await receiver.ReceivedAsync(this, envelopes.ToArray());
                        }
                    }
                    else
                    {
                        // Slow down if this is a periodically used queue
                        await Task.Delay(250.Milliseconds());
                    }
                }
                catch (TaskCanceledException)
                {
                    // do nothing here, it's all good
                }
                catch (Exception e)
                {
                    failedCount++;
                    var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                    logger.LogError(e, "Error while trying to retrieve messages from SQS Queue {Uri}",
                        queue.Uri);
                    await Task.Delay(pauseTime);
                }
            }
        }, _cancellation.Token);
    }

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
        if (!_cancellation.IsCancellationRequested)
        {
            await _cancellation.CancelAsync();
        }

        _cancellation.Dispose();
        _requeueBlock.Dispose();
        _deadLetterBlock?.Dispose();
        _task.SafeDispose();
    }

    public Uri Address => _queue.Uri;

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();

        try
        {
            await _task.WaitAsync(_drainTimeout);
        }
        catch (Exception e)
        {
            if (e is not TaskCanceledException)
            {
                _logger.LogDebug(e, "Error waiting for SQS polling task to complete during shutdown for {Uri}", _queue.Uri);
            }
        }
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
