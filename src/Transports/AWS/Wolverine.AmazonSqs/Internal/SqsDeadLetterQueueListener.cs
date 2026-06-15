using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

/// <summary>
/// Configuration holder for Amazon SQS dead letter queue recovery. Registered as a singleton
/// so the listener can discover which dead letter queues to drain. When no queue names are
/// supplied, the listener recovers from every distinct dead-letter queue used by a listening
/// SQS queue.
/// </summary>
public class AmazonSqsDeadLetterQueueRecoverySettings
{
    public List<string> QueueNames { get; } = new();
}

/// <summary>
/// Background service that drains one or more Amazon SQS dead letter queues and recovers the
/// messages into Wolverine's durable dead letter storage (the <c>wolverine_dead_letters</c> table).
/// This bridges SQS's native dead-lettering with Wolverine's database-backed dead letter management,
/// so natively dead-lettered messages become queryable and replayable through <see cref="Wolverine.Persistence.Durability.IDeadLetters"/>
/// and tools like CritterWatch. This mirrors the RabbitMQ <c>EnableDeadLetterQueueRecovery()</c> feature.
/// </summary>
public class SqsDeadLetterQueueListener : BackgroundService
{
    private readonly AmazonSqsTransport _transport;
    private readonly IWolverineRuntime _runtime;
    private readonly AmazonSqsDeadLetterQueueRecoverySettings _settings;
    private readonly ILogger<SqsDeadLetterQueueListener> _logger;

    public SqsDeadLetterQueueListener(AmazonSqsTransport transport, IWolverineRuntime runtime,
        AmazonSqsDeadLetterQueueRecoverySettings settings, ILogger<SqsDeadLetterQueueListener> logger)
    {
        _transport = transport;
        _runtime = runtime;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The transport's SQS client is created when Wolverine connects the transport during
            // startup. Wait for it rather than building a second client so we share configuration,
            // credentials, and (for LocalStack) the service URL.
            await waitForClientAsync(stoppingToken);

            var queueNames = resolveQueueNames();
            if (queueNames.Count == 0)
            {
                _logger.LogInformation(
                    "Amazon SQS dead letter queue recovery was enabled, but no dead letter queues could be resolved. No recovery listeners were started.");
                return;
            }

            var tasks = queueNames.Select(name => drainQueueLoopAsync(name, stoppingToken)).ToArray();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Amazon SQS dead letter queue recovery listener failed");
        }
    }

    private async Task waitForClientAsync(CancellationToken token)
    {
        while (_transport.Client == null)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(250.Milliseconds(), token);
        }
    }

    private List<string> resolveQueueNames()
    {
        if (_settings.QueueNames.Count > 0)
        {
            return _settings.QueueNames.Distinct().ToList();
        }

        return _transport.Queues
            .Where(x => x.IsListener)
            .Select(x => x.DeadLetterQueueName)
            .Where(x => x.IsNotEmpty())
            .Distinct()
            .Select(x => x!)
            .ToList();
    }

    private async Task drainQueueLoopAsync(string queueName, CancellationToken stoppingToken)
    {
        var queue = _transport.Queues[queueName];
        var failedCount = 0;

        _logger.LogInformation(
            "Started Amazon SQS dead letter queue recovery listener on queue '{QueueName}'. Messages will be recovered to durable storage.",
            queueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (queue.QueueUrl.IsEmpty())
                {
                    await queue.InitializeAsync(_logger);
                }

                var request = new ReceiveMessageRequest(queue.QueueUrl)
                {
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 5,
                    VisibilityTimeout = 60,
                    MessageAttributeNames = ["All"]
                };

                var results = await _transport.Client!.ReceiveMessageAsync(request, stoppingToken);

                failedCount = 0;

                if (results.Messages == null || results.Messages.Count == 0)
                {
                    await Task.Delay(250.Milliseconds(), stoppingToken);
                    continue;
                }

                foreach (var message in results.Messages)
                {
                    await recoverMessageAsync(queue, message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                failedCount++;
                var pause = failedCount > 5 ? 5.Seconds() : (failedCount * 200).Milliseconds();
                _logger.LogError(e,
                    "Error while recovering dead letters from Amazon SQS queue '{QueueName}'", queueName);
                await Task.Delay(pause, stoppingToken);
            }
        }
    }

    private async Task recoverMessageAsync(AmazonSqsQueue queue, Message message, CancellationToken token)
    {
        try
        {
            var envelope = new Envelope();
            string exceptionType;
            string exceptionMessage;

            try
            {
                var mapper = queue.BuildMapper(_runtime);
                mapper.ReadEnvelopeData(envelope, message.Body, message.MessageAttributes ?? new Dictionary<string, MessageAttributeValue>());

                // Wolverine stamps these onto the envelope headers when it moves a failed message to
                // the dead letter queue (see DeadLetterQueueConstants.StampFailureMetadata).
                exceptionType = headerOrDefault(envelope, DeadLetterQueueConstants.ExceptionTypeHeader, "Unknown");
                exceptionMessage = headerOrDefault(envelope, DeadLetterQueueConstants.ExceptionMessageHeader,
                    "Recovered from Amazon SQS dead letter queue");
            }
            catch (Exception readError)
            {
                // The dead letter body wasn't a Wolverine envelope (for example, a message moved by a
                // native SQS redrive policy from a non-Wolverine producer). Preserve it with a minimal
                // envelope so it is still visible and not silently lost.
                _logger.LogWarning(readError,
                    "Could not reconstruct a Wolverine envelope from SQS dead letter message {MessageId} on '{QueueName}'. Persisting a minimal envelope.",
                    message.MessageId, queue.QueueName);

                envelope = new Envelope
                {
                    Data = System.Text.Encoding.UTF8.GetBytes(message.Body ?? string.Empty),
                    ContentType = EnvelopeConstants.JsonContentType,
                    MessageType = "unknown"
                };
                exceptionType = "Unknown";
                exceptionMessage = "Recovered from Amazon SQS dead letter queue (non-Wolverine message)";
            }

            if (envelope.Id == Guid.Empty)
            {
                envelope.Id = Guid.NewGuid();
            }

            envelope.Destination ??= queue.Uri;
            envelope.Source ??= queue.Uri.ToString();
            if (envelope.SentAt == default)
            {
                envelope.SentAt = DateTimeOffset.UtcNow;
            }

            var exception = new DeadLetterRecoveredException(exceptionType, exceptionMessage);
            await _runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);

            // Only delete once the dead letter has been safely persisted.
            await _transport.Client!.DeleteMessageAsync(queue.QueueUrl, message.ReceiptHandle, token);

            _logger.LogInformation(
                "Recovered dead letter {MessageId} (type={MessageType}) from Amazon SQS queue '{QueueName}' to durable storage.",
                envelope.Id, envelope.MessageType ?? "unknown", queue.QueueName);
        }
        catch (Exception e)
        {
            // Leave the message on the queue (its visibility timeout returns it) so a transient
            // storage failure doesn't lose the dead letter.
            _logger.LogError(e,
                "Failed to recover SQS dead letter message {MessageId} from '{QueueName}'", message.MessageId,
                queue.QueueName);
        }
    }

    private static string headerOrDefault(Envelope envelope, string key, string fallback)
    {
        return envelope.Headers.TryGetValue(key, out var value) && value.IsNotEmpty() ? value! : fallback;
    }
}
