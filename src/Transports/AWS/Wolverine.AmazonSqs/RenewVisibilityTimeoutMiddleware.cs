using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs;

public class RenewVisibilityTimeoutMiddleware
{
    private readonly CancellationTokenSource _activeTokenSource;
    private readonly Guid _envelopeId;
    private readonly ILogger _logger;
    //  SQS Max Visibility Tiemout is 12 hours - https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-visibility-timeout.html
    private readonly TimeSpan _maxVisibilityTimeout = TimeSpan.FromHours(12);
    private readonly string? _messageType;
    private readonly int _queueVisibilityTimeout = 30;
    private readonly string? _queueUrl;
    private readonly string _receiptHandle;
    private readonly Task _renewVisibilityTimeoutTask;
    private readonly DateTime _startedAt;
    private readonly AmazonSqsTransport _transport;

    public RenewVisibilityTimeoutMiddleware(Envelope envelope, ILogger logger)
    {
        if (envelope is AmazonSqsEnvelope { Listener: SqsListener listener } sqsEnvelope)
        {
            _transport = listener.GetTransport();
            _receiptHandle = sqsEnvelope.SqsMessage.ReceiptHandle;
            _startedAt = DateTime.UtcNow;
            
            var queue = listener.GetQueue();
            _queueVisibilityTimeout = Math.Min( _queueVisibilityTimeout, queue.VisibilityTimeout);
            _queueUrl = queue.QueueUrl;
        }

        _envelopeId = envelope.Id;
        _messageType = envelope.MessageType;
        _logger = logger;
        _activeTokenSource = new CancellationTokenSource();
        _renewVisibilityTimeoutTask = Task.Run(RenewVisibilityTimeout);
    }

    private async Task RenewVisibilityTimeout()
    {
        if (_queueUrl is null) return;
        
        var delay = TimeSpan.FromSeconds(_queueVisibilityTimeout * 0.7);

        while (!_activeTokenSource.IsCancellationRequested)
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _activeTokenSource.Token)
                        .ContinueWith(t => t, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default).ConfigureAwait(false);

                if (_activeTokenSource.IsCancellationRequested)
                    break;

                await _transport.Client!.ChangeMessageVisibilityAsync(_queueUrl, _receiptHandle,
                    _queueVisibilityTimeout);

                if (DateTime.UtcNow - _startedAt.AddSeconds(_queueVisibilityTimeout) >= _maxVisibilityTimeout)
                    break;
            }
            catch (MessageNotInflightException exception)
            {
                _logger.LogWarning(exception,
                    "Envelope ({EnvelopeId} / {MessageType} / {ReceiptHandle}) no longer in flight", _envelopeId,
                    _messageType, _receiptHandle);

                break;
            }
            catch (ReceiptHandleIsInvalidException exception)
            {
                //  If the endpoint is Durable or Buffered this might happen
                _logger.LogWarning(exception,
                    "Envelope ({EnvelopeId} / {MessageType} / {ReceiptHandle}) receipt handle is invalid", _envelopeId,
                    _messageType, _receiptHandle);

                break;
            }
            catch (AmazonSQSException exception)
            {
                //  If the endpoint is Durable or Buffered this might happen with localstack
                _logger.LogError(exception,
                    "Error extending envelope ({EnvelopeId} / {MessageType} / {ReceiptHandle}) visibility timeout to {VisibilityTimeout} seconds ({ElapsedTime})",
                    _envelopeId, _messageType, _receiptHandle, _queueVisibilityTimeout, DateTime.UtcNow - _startedAt);
                break;
            }
            catch (Exception)
            {
                break;
            }
    }

    public void Finally()
    {
        _activeTokenSource.Cancel();
        _renewVisibilityTimeoutTask.SafeDispose();
    }
}