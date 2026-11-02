using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
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
            var connection = await _transport.CreateConnectionAsync();
            _connection = connection;
            var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
            _channel = channel;

            // Prefetch 10 messages at a time to avoid overwhelming the database
            await channel.BasicQosAsync(0, 10, false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, args) =>
            {
                try
                {
                    await processDeadLetterAsync(args, stoppingToken);
                    await channel.BasicAckAsync(args.DeliveryTag, false, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process dead letter message from RabbitMQ DLQ");

                    try
                    {
                        // Requeue the message so we don't lose it
                        await channel.BasicNackAsync(args.DeliveryTag, false, true, stoppingToken);
                    }
                    catch (Exception nackFailure) when (isShutdownException(nackFailure))
                    {
                        // The channel closed underneath us, and RabbitMQ requeues anything left unacked anyway
                    }
                }
            };

            foreach (var queueName in queueNames)
            {
                await channel.BasicConsumeAsync(queueName, false, consumer, stoppingToken);
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
        finally
        {
            // Picks up whatever a stop that ran while this was still connecting could not see yet
            await closeAsync(CancellationToken.None);
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
        // GH-5314. A host can be stopped twice at the same time, e.g. WebApplicationFactory's teardown racing
        // RunJasperFxCommands, so this is idempotent, safe to call concurrently, and never throws
        await closeAsync(cancellationToken);

        await base.StopAsync(cancellationToken);
    }

    private async Task closeAsync(CancellationToken cancellationToken)
    {
        // Both fields are claimed before any await, so a concurrent caller finds nothing left to close and
        // can never close the connection underneath a channel close that is still in flight
        var channel = Interlocked.Exchange(ref _channel, null);
        var connection = Interlocked.Exchange(ref _connection, null);

        if (channel is not null)
        {
            try
            {
                if (channel.IsOpen)
                {
                    await channel.CloseAsync(cancellationToken);
                }
            }
            catch (Exception e)
            {
                logCloseFailure(e, "channel");
            }

            disposeQuietly(channel, "channel");
        }

        if (connection is not null)
        {
            try
            {
                if (connection.IsOpen)
                {
                    await connection.CloseAsync(cancellationToken);
                }
            }
            catch (Exception e)
            {
                logCloseFailure(e, "connection");
            }

            disposeQuietly(connection, "connection");
        }
    }

    private void disposeQuietly(IDisposable disposable, string what)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception e)
        {
            logCloseFailure(e, what);
        }
    }

    private void logCloseFailure(Exception e, string what)
    {
        if (isShutdownException(e))
        {
            // Already closed by the broker, by the connection, or by another stop: nothing left to do
            _logger.LogDebug(e, "The RabbitMQ dead letter queue listener's {What} was already closed while stopping", what);
        }
        else
        {
            _logger.LogWarning(e, "Error closing the RabbitMQ dead letter queue listener's {What} while stopping", what);
        }
    }

    private static bool isShutdownException(Exception e)
    {
        // AlreadyClosedException is an OperationInterruptedException
        return e is OperationInterruptedException or ObjectDisposedException or OperationCanceledException;
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
