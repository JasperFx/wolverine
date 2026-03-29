using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

/// <summary>
/// Configuration holder for dead letter queue recovery. Registered as a singleton
/// so the listener can discover which queues to subscribe to.
/// </summary>
public class DeadLetterQueueRecoverySettings
{
    public List<string> QueueNames { get; } = new();
}

/// <summary>
/// Background service that listens to one or more RabbitMQ dead letter queues and recovers
/// messages into Wolverine's persistent dead letter storage (wolverine_dead_letters table).
/// This bridges the gap between RabbitMQ's native DLX mechanism and Wolverine's database-backed
/// dead letter management, enabling CritterWatch to query, replay, and discard dead letters.
/// </summary>
public class DeadLetterQueueListener : BackgroundService
{
    private readonly RabbitMqTransport _transport;
    private readonly IWolverineRuntime _runtime;
    private readonly DeadLetterQueueRecoverySettings _settings;
    private readonly ILogger<DeadLetterQueueListener> _logger;
    private IChannel? _channel;
    private IConnection? _connection;

    public DeadLetterQueueListener(RabbitMqTransport transport, IWolverineRuntime runtime,
        DeadLetterQueueRecoverySettings settings, ILogger<DeadLetterQueueListener> logger)
    {
        _transport = transport;
        _runtime = runtime;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueNames = _settings.QueueNames.Count > 0
            ? _settings.QueueNames
            : new List<string> { _transport.DeadLetterQueue.QueueName };

        try
        {
            _connection = await _transport.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

            // Prefetch 10 messages at a time to avoid overwhelming the database
            await _channel.BasicQosAsync(0, 10, false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (_, args) =>
            {
                try
                {
                    await processDeadLetterAsync(args, stoppingToken);
                    await _channel.BasicAckAsync(args.DeliveryTag, false, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process dead letter message from RabbitMQ DLQ");
                    // Requeue the message so we don't lose it
                    await _channel.BasicNackAsync(args.DeliveryTag, false, true, stoppingToken);
                }
            };

            foreach (var queueName in queueNames)
            {
                await _channel.BasicConsumeAsync(queueName, false, consumer, stoppingToken);
                _logger.LogInformation(
                    "Dead letter queue listener started on queue '{QueueName}'. Messages will be recovered to database storage.",
                    queueName);
            }

            // Keep running until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dead letter queue listener failed");
        }
    }

    private async Task processDeadLetterAsync(BasicDeliverEventArgs args, CancellationToken ct)
    {
        var envelope = new Envelope
        {
            Data = args.Body.ToArray(),
            ContentType = args.BasicProperties.ContentType ?? EnvelopeConstants.JsonContentType,
        };

        // Map standard RabbitMQ properties to Wolverine envelope
        if (Guid.TryParse(args.BasicProperties.MessageId, out var messageId))
        {
            envelope.Id = messageId;
        }
        else
        {
            envelope.Id = Guid.NewGuid();
        }

        envelope.MessageType = args.BasicProperties.Type;
        envelope.CorrelationId = args.BasicProperties.CorrelationId;

        // Copy all headers from the message
        if (args.BasicProperties.Headers != null)
        {
            foreach (var header in args.BasicProperties.Headers)
            {
                var value = header.Value switch
                {
                    byte[] b => Encoding.UTF8.GetString(b),
                    _ => header.Value?.ToString()
                };

                if (value != null)
                {
                    envelope.Headers[header.Key] = value;
                }
            }
        }

        // Extract exception info from Wolverine headers (InteropFriendly mode stamps these)
        var exceptionType = extractHeader(args.BasicProperties.Headers,
            DeadLetterQueueConstants.ExceptionTypeHeader) ?? "Unknown";
        var exceptionMessage = extractHeader(args.BasicProperties.Headers,
            DeadLetterQueueConstants.ExceptionMessageHeader) ?? "Recovered from RabbitMQ dead letter queue";

        // Extract x-death metadata from RabbitMQ (added by the DLX mechanism)
        var (originalQueue, deathReason, deathCount) = extractXDeathInfo(args.BasicProperties.Headers);

        if (string.IsNullOrEmpty(exceptionMessage) || exceptionMessage == "Recovered from RabbitMQ dead letter queue")
        {
            // Build a descriptive message from RabbitMQ's x-death metadata
            var parts = new List<string> { "Message dead-lettered by RabbitMQ" };
            if (!string.IsNullOrEmpty(originalQueue)) parts.Add($"from queue '{originalQueue}'");
            if (!string.IsNullOrEmpty(deathReason)) parts.Add($"reason: {deathReason}");
            if (deathCount > 0) parts.Add($"death count: {deathCount}");
            exceptionMessage = string.Join(", ", parts);
        }

        // Reconstruct source and destination info
        if (!string.IsNullOrEmpty(originalQueue))
        {
            envelope.Source = $"rabbitmq://queue/{originalQueue}";
            envelope.Destination = new Uri($"rabbitmq://queue/{originalQueue}");
        }
        else
        {
            // Fallback — use the DLQ queue name itself
            envelope.Destination = new Uri($"rabbitmq://queue/{args.Exchange ?? "unknown"}");
        }

        // Ensure SentAt is set (needed for dead letter storage)
        if (envelope.SentAt == default)
        {
            envelope.SentAt = DateTimeOffset.UtcNow;
        }

        // Create a synthetic exception to pass to the dead letter storage
        var exception = new DeadLetterRecoveredException(exceptionType, exceptionMessage);

        // Write to the message store's dead letter storage
        await _runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);

        _logger.LogInformation(
            "Recovered dead letter {MessageId} (type={MessageType}) from RabbitMQ DLQ to database storage. " +
            "Original queue: {OriginalQueue}, Reason: {Reason}",
            envelope.Id, envelope.MessageType ?? "unknown", originalQueue ?? "unknown",
            deathReason ?? "unknown");
    }

    private static string? extractHeader(IDictionary<string, object?>? headers, string key)
    {
        if (headers == null) return null;
        if (!headers.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            byte[] b => Encoding.UTF8.GetString(b),
            _ => raw?.ToString()
        };
    }

    private static (string? originalQueue, string? reason, long count) extractXDeathInfo(
        IDictionary<string, object?>? headers)
    {
        if (headers == null) return (null, null, 0);
        if (!headers.TryGetValue("x-death", out var xDeathRaw)) return (null, null, 0);

        // x-death is a list of dictionaries added by RabbitMQ when a message is dead-lettered
        if (xDeathRaw is not IList<object> xDeathList || xDeathList.Count == 0)
            return (null, null, 0);

        // Take the first (most recent) death record
        if (xDeathList[0] is not IDictionary<string, object> firstDeath)
            return (null, null, 0);

        string? queue = null;
        string? reason = null;
        long count = 0;

        if (firstDeath.TryGetValue("queue", out var q))
            queue = q switch { byte[] b => Encoding.UTF8.GetString(b), _ => q?.ToString() };

        if (firstDeath.TryGetValue("reason", out var r))
            reason = r switch { byte[] b => Encoding.UTF8.GetString(b), _ => r?.ToString() };

        if (firstDeath.TryGetValue("count", out var c) && c is long l)
            count = l;

        return (queue, reason, count);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync(cancellationToken);
            _channel.Dispose();
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync(cancellationToken);
            _connection.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// Synthetic exception type used when recovering dead letters from RabbitMQ.
/// Carries the original exception type name and message reconstructed from headers.
/// </summary>
public class DeadLetterRecoveredException : Exception
{
    public string OriginalExceptionType { get; }

    public DeadLetterRecoveredException(string originalExceptionType, string message)
        : base(message)
    {
        OriginalExceptionType = originalExceptionType;
    }

    public override string ToString() => $"{OriginalExceptionType}: {Message}";
}
