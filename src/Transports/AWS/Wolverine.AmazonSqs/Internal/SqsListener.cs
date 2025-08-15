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

    public SqsListener(IWolverineRuntime runtime, AmazonSqsQueue queue, AmazonSqsTransport transport,
        IReceiver receiver)
    {
        if (transport.Client == null)
        {
            throw new InvalidOperationException("Parent transport has not been initialized");
        }

        var logger = runtime.LoggerFactory.CreateLogger<SqsListener>();
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

    public ValueTask DisposeAsync()
    {
        _requeueBlock.Dispose();
        _cancellation.Cancel();
        _deadLetterBlock?.Dispose();

        _task.SafeDispose();
        return ValueTask.CompletedTask;
    }

    public Uri Address => _queue.Uri;

    public ValueTask StopAsync()
    {
        return DisposeAsync();
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
        return _deadLetterBlock!.PostAsync(envelope);
    }

    public bool NativeDeadLetterQueueEnabled { get; }

    private AmazonSqsEnvelope buildEnvelope(Message message)
    {
        var envelope = new AmazonSqsEnvelope(message);
        _queue.Mapper.ReadEnvelopeData(envelope, message.Body, message.MessageAttributes);

        return envelope;
    }

    public Task CompleteAsync(Message sqsMessage)
    {
        return _transport.Client!.DeleteMessageAsync(_queue.QueueUrl, sqsMessage.ReceiptHandle);
    }
}