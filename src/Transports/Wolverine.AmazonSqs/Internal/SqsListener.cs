using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ILogger _logger;
    private readonly AmazonSqsMapper _mapper;
    private readonly AmazonSqsQueue _queue;
    private readonly Task _task;
    private readonly AmazonSqsTransport _transport;

    public SqsListener(IWolverineRuntime runtime, AmazonSqsQueue queue, AmazonSqsTransport transport,
        IReceiver receiver)
    {
        if (transport.Client == null)
        {
            throw new InvalidOperationException("Parent transport has not been initialized");
        }

        _mapper = new AmazonSqsMapper(queue, runtime);
        _logger = runtime.LoggerFactory.CreateLogger<SqsListener>();
        _queue = queue;
        _transport = transport;

        var headers = _mapper.AllHeaders().ToList();

        var failedCount = 0;

        _task = Task.Run(async () =>
        {
            while (!_cancellation.Token.IsCancellationRequested)
            {
                try
                {
                    var request = new ReceiveMessageRequest(_queue.QueueUrl)
                    {
                        MessageAttributeNames = headers
                    };
                    
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
                                _mapper.MapIncomingToEnvelope(envelope, message);

                                envelopes.Add(envelope);
                            }
                            catch (Exception e)
                            {
                                await tryMoveToDeadLetterQueue(_transport.Client, message);
                                _logger.LogError(e, "Error while reading message {Id} from {Uri}", message.MessageId,
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
                catch (Exception e)
                {
                    failedCount++;
                    var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                    _logger.LogError(e, "Error while trying to retrieve messages from Azure Service Bus {Uri}",
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

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            return new ValueTask(DeferAsync(e.SqsMessage));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
        return ValueTask.CompletedTask;
    }

    public Uri Address => _queue.Uri;

    public ValueTask StopAsync()
    {
        return DisposeAsync();
    }

    private Task tryMoveToDeadLetterQueue(IAmazonSQS client, Message message)
    {
        return Task.CompletedTask;
    }

    private AmazonSqsEnvelope buildEnvelope(Message message)
    {
        var envelope = new AmazonSqsEnvelope(message);
        _mapper.MapIncomingToEnvelope(envelope, message);
        envelope.Data = Encoding.Default.GetBytes(message.Body);
        return envelope;
    }

    public Task CompleteAsync(Message sqsMessage)
    {
        return _transport.Client!.DeleteMessageAsync(_queue.QueueUrl, sqsMessage.ReceiptHandle);
    }

    public Task DeferAsync(Message sqsMessage)
    {
        // TODO -- the visibility timeout needs to be configurable by timeout
        return _transport.Client!.ChangeMessageVisibilityAsync(_queue.QueueUrl, sqsMessage.ReceiptHandle, 1000);
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            await DeferAsync(e.SqsMessage);
            return true;
        }

        return false;
    }
}