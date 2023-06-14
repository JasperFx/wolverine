using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsListener : IListener, ISupportDeadLetterQueue
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RetryBlock<Envelope>? _deadLetterBlock;
    private readonly AmazonSqsQueue? _deadLetterQueue;
    private readonly AmazonSqsQueue _queue;
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

        if (_queue.DeadLetterQueueName != null)
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

                    if (results.Messages.Any())
                    {
                        var envelopes = new List<Envelope>();
                        foreach (var message in results.Messages)
                        {
                            try
                            {
                                var envelope = buildEnvelope(message);

                                envelopes.Add(envelope);
                            }
                            catch (Exception e)
                            {
                                await tryMoveToDeadLetterQueue(_transport.Client, message);
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

                    logger.LogError(e, "Error while trying to retrieve messages from Azure Service Bus {Uri}",
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
        if (_deadLetterBlock != null)
        {
            _deadLetterBlock.Dispose();
        }

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

    private Task tryMoveToDeadLetterQueue(IAmazonSQS client, Message message)
    {
        // TODO -- do something here!
        return Task.CompletedTask;
    }

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