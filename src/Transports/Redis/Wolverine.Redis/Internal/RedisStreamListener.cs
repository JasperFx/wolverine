using System.Diagnostics;
using System.Linq;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Redis.Internal;

public class RedisStreamListener : IListener, ISupportDeadLetterQueue
{
    private readonly RedisTransport _transport;
    private readonly RedisStreamEndpoint _endpoint;
    private readonly IWolverineRuntime _runtime;
    private readonly IReceiver _receiver;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DurabilitySettings _settings;

    private Task? _consumerTask;
    private Task? _scheduledTask;
    private ListeningStatus _status = ListeningStatus.Stopped;
    private string _consumerName;



    public RedisStreamListener(RedisTransport transport, RedisStreamEndpoint endpoint,
        IWolverineRuntime runtime, IReceiver receiver)
    {
        _transport = transport;
        _endpoint = endpoint;
        _runtime = runtime;
        _receiver = receiver;
        _logger = runtime.LoggerFactory.CreateLogger<RedisStreamListener>();
        _settings = runtime.DurabilitySettings;

        // Generate stable consumer name: service name + node number (+ machine) by default,
        // or use endpoint-level override if specified.
        _consumerName = _transport.ComputeConsumerName(_runtime, _endpoint);

        Address = endpoint.Uri;
    }

    public Uri Address { get; }
    public ListeningStatus Status => _status;
    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    internal bool DeleteOnAck => _transport.DeleteStreamEntryOnAck;
    
    // ISupportDeadLetterQueue implementation
    public bool NativeDeadLetterQueueEnabled => _endpoint.NativeDeadLetterQueueEnabled;
    
    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        try
        {
            var database = _transport.GetDatabase(database: _endpoint.DatabaseId);
            
            // Serialize the envelope
            var serializedEnvelope = EnvelopeSerializer.Serialize(envelope);
            
            // Build the dead letter entry with error information
            var fields = new List<NameValueEntry>
            {
                new("envelope", serializedEnvelope),
                new(DeadLetterQueueConstants.ExceptionTypeHeader, exception.GetType().FullName ?? "Unknown"),
                new(DeadLetterQueueConstants.ExceptionMessageHeader, exception.Message ?? ""),
                new(DeadLetterQueueConstants.ExceptionStackHeader, exception.StackTrace ?? ""),
                new(DeadLetterQueueConstants.FailedAtHeader, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()),
                new("message-type", envelope.MessageType ?? "Unknown"),
                new("envelope-id", envelope.Id.ToString()),
                new("attempts", envelope.Attempts.ToString())
            };
            
            // Add the dead letter entry to the dead letter stream
            var deadLetterMessageId = await database.StreamAddAsync(_endpoint.DeadLetterQueueKey, fields.ToArray());
            
            _logger.LogInformation("Moved envelope {EnvelopeId} to dead letter queue {DeadLetterKey} with message ID {DeadLetterMessageId}. Exception: {ExceptionType}: {ExceptionMessage}",
                envelope.Id, _endpoint.DeadLetterQueueKey, deadLetterMessageId, exception.GetType().Name, exception.Message);
            
            // Acknowledge the original message if we have the Redis entry ID
            if (envelope.Headers.TryGetValue(RedisEnvelopeMapper.RedisEntryIdHeader, out var idString) && !string.IsNullOrEmpty(idString))
            {
                try
                {
                    if (DeleteOnAck)
                        await database.StreamAcknowledgeAndDeleteAsync(_endpoint.StreamKey, _endpoint.ConsumerGroup!, StreamTrimMode.Acknowledged, idString!);
                    else
                        await database.StreamAcknowledgeAsync(_endpoint.StreamKey, _endpoint.ConsumerGroup!, idString!);
                }
                catch (Exception ackEx)
                {
                    _logger.LogWarning(ackEx, "Error ACKing message {EnvelopeId} after moving to dead letter queue", envelope.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move envelope {EnvelopeId} to dead letter queue {DeadLetterKey}",
                envelope.Id, _endpoint.DeadLetterQueueKey);
            throw;
        }
    }

    // ISupportNativeScheduling implementation
    public async ValueTask InitializeAsync()
    {
        // Only create resources at listener init time if AutoProvision is enabled.
        if (_transport.AutoProvision)
        {
            await _endpoint.SetupAsync(_logger);
        }
        else
        {
            // Fail-fast if the required consumer group (or stream) is missing when AutoProvision is disabled.
            if (_endpoint.IsListener && !string.IsNullOrEmpty(_endpoint.ConsumerGroup))
            {
                try
                {
                    var db = _transport.GetDatabase(database: _endpoint.DatabaseId);
                    var groups = await db.StreamGroupInfoAsync(_endpoint.StreamKey);
                    var exists = groups?.Any(g => g.Name == _endpoint.ConsumerGroup) ?? false;
                    if (!exists)
                    {
                        throw new InvalidOperationException($"Redis consumer group '{_endpoint.ConsumerGroup}' for stream '{_endpoint.StreamKey}' (db {_endpoint.DatabaseId}) does not exist, and AutoProvision is disabled. Enable AutoProvision() or run AddResourceSetupOnStartup() to create it.");
                    }
                }
                catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Redis stream '{_endpoint.StreamKey}' (db {_endpoint.DatabaseId}) does not exist, and AutoProvision is disabled. Create the stream and consumer group '{_endpoint.ConsumerGroup}' or enable AutoProvision()/AddResourceSetupOnStartup().", ex);
                }
            }
        }

        // Start processing loops
        if (_status == ListeningStatus.Stopped)
        {
            _logger.LogInformation("Starting Redis stream listener for {StreamKey} with consumer group {ConsumerGroup} and consumer {ConsumerName}",
                _endpoint.StreamKey, _endpoint.ConsumerGroup, _consumerName);

            _status = ListeningStatus.Accepting;

            // Start the consumer loop
            _consumerTask = Task.Run(ConsumerLoop, _cancellation.Token);
            
            // Start the scheduled messages polling loop
            _scheduledTask = Task.Run(LookForScheduledMessagesAsync, _cancellation.Token);
        }
    }

    public async ValueTask StopAsync()
    {
        if (_status == ListeningStatus.Stopped)
        {
            return;
        }

        _logger.LogInformation("Stopping Redis stream listener for {StreamKey}", _endpoint.StreamKey);

        _status = ListeningStatus.Stopped;
        _cancellation.Cancel();

        if (_consumerTask != null)
        {
            try
            {
                await _consumerTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TaskCanceledException)
            {
                // Expected when cancellation token is used
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while stopping consumer task for stream {StreamKey}", _endpoint.StreamKey);
            }
        }
        
        if (_scheduledTask != null)
        {
            try
            {
                await _scheduledTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TaskCanceledException)
            {
                // Expected when cancellation token is used
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while stopping scheduled task for stream {StreamKey}", _endpoint.StreamKey);
            }
        }
    }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        try
        {
            if (!envelope.Headers.TryGetValue(RedisEnvelopeMapper.RedisEntryIdHeader, out var idString) || string.IsNullOrEmpty(idString))
            {
                _logger.LogDebug("No Redis stream id header present for envelope {EnvelopeId}; skipping ACK", envelope.Id);
                return;
            }

            var db = _transport.GetDatabase();
            if (DeleteOnAck)
                await db.StreamAcknowledgeAndDeleteAsync(_endpoint.StreamKey, _endpoint.ConsumerGroup!, StreamTrimMode.Acknowledged, idString!);
            else
                await db.StreamAcknowledgeAsync(_endpoint.StreamKey, _endpoint.ConsumerGroup!, idString!);
            _logger.LogDebug("Acknowledged Redis stream message {StreamId} on {StreamKey}", idString, _endpoint.StreamKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error ACKing Redis stream message for envelope {EnvelopeId}", envelope.Id);
        }
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        try
        {
            var db = _transport.GetDatabase();

            // 1) Ack the current pending entry if we can
            if (envelope.Headers.TryGetValue(RedisEnvelopeMapper.RedisEntryIdHeader, out var idString) && !string.IsNullOrEmpty(idString))
            {
                try
                {
                    if(DeleteOnAck)
                        await db.StreamAcknowledgeAndDeleteAsync(_endpoint.StreamKey, _endpoint.ConsumerGroup!, StreamTrimMode.Acknowledged, idString!);
                    else
                        await db.StreamAcknowledgeAsync(_endpoint.StreamKey, _endpoint.ConsumerGroup!, idString!);
                }
                catch (Exception ackEx)
                {
                    _logger.LogWarning(ackEx, "Error ACKing Redis stream message before requeue for envelope {EnvelopeId}", envelope.Id);
                }
            }

            // 2) Re-add a copy to the tail of the stream
            _endpoint.EnvelopeMapper ??= _endpoint.BuildMapper(_runtime);
            var fields = new List<NameValueEntry>();
            _endpoint.EnvelopeMapper.MapEnvelopeToOutgoing(envelope, fields);
            var newId = await db.StreamAddAsync(_endpoint.StreamKey, fields.ToArray());

            _logger.LogDebug("Requeued envelope {EnvelopeId} to Redis stream {StreamKey} as new entry {NewId}",
                envelope.Id, _endpoint.StreamKey, newId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue Redis stream message for envelope {EnvelopeId}", envelope.Id);
        }
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        await DeferAsync(envelope);
        return true;
    }

    private async Task EnsureGroupExistsAsync(IDatabase db)
    {
        if (string.IsNullOrEmpty(_endpoint.ConsumerGroup)) return;
        if (!_transport.AutoProvision) return;

        try
        {
            // Use the endpoint's StartFrom setting to determine position
            var startPosition = _endpoint.StartFrom == StartFrom.Beginning ? "0-0" : "$";
            await db.StreamCreateConsumerGroupAsync(_endpoint.StreamKey, _endpoint.ConsumerGroup, startPosition, true);
            _logger.LogInformation("Ensured consumer group {Group} exists for stream {StreamKey}", _endpoint.ConsumerGroup, _endpoint.StreamKey);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists, nothing to do
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure consumer group {Group} exists for stream {StreamKey}", _endpoint.ConsumerGroup, _endpoint.StreamKey);
        }
    }

    private async Task<StreamEntry[]> ReadEntriesAsync(IDatabase database, bool useAutoClaim)
    {
        if (useAutoClaim)
        {
            var minIdleMs = (int)_endpoint.AutoClaimMinIdle.TotalMilliseconds;
            // XAUTOCLAIM for pending messages
            var result = await database.StreamAutoClaimAsync(
                _endpoint.StreamKey,
                _endpoint.ConsumerGroup!,
                _consumerName,
                minIdleMs,
                "0-0", // start from the beginning of PEL
                _endpoint.BatchSize);

            _logger.LogDebug(
                "XAUTOCLAIM on {StreamKey}/{Group} (minIdle={MinIdle}ms) returned {Count} entries; nextStartId={NextStartId}",
                _endpoint.StreamKey, _endpoint.ConsumerGroup, minIdleMs, result.ClaimedEntries?.Length ?? 0,
                result.NextStartId);

            return result.ClaimedEntries ?? [];
        }

        // Standard XREADGROUP for new messages
        return await database.StreamReadGroupAsync(
            _endpoint.StreamKey,
            _endpoint.ConsumerGroup!,
            _consumerName,
            ">", // Read new messages not yet delivered to this consumer group
            count: _endpoint.BatchSize,
            noAck: false);
    }

    private async Task ConsumerLoop()
    {
        var database = _transport.GetDatabase();
        var autoClaimWatch = Stopwatch.StartNew();

        try
        {
            while (!_cancellation.Token.IsCancellationRequested && _status == ListeningStatus.Accepting)
            {
                try
                {
                    // Determine if it's time to use AutoClaim instead of regular read
                    var shouldUseAutoClaim = _endpoint.AutoClaimEnabled &&
                                           autoClaimWatch.Elapsed >= _endpoint.AutoClaimPeriod;

                    // Read from either XREADGROUP or XAUTOCLAIM
                    var streamResults = await ReadEntriesAsync(database, shouldUseAutoClaim);

                    if (shouldUseAutoClaim)
                    {
                        autoClaimWatch.Restart();
                        _logger.LogDebug("Used XAUTOCLAIM for {StreamKey}, found {Count} entries",
                            _endpoint.StreamKey, streamResults.Length);
                    }
                    else
                    {
                        _logger.LogDebug("Read {Count} entries from {StreamKey} for group {Group} consumer {Consumer}",
                            streamResults.Length, _endpoint.StreamKey, _endpoint.ConsumerGroup, _consumerName);
                    }

                    if (!streamResults.Any())
                    {
                        // No messages, wait a bit before polling again
                        await Task.Delay(_endpoint.BlockTimeoutMilliseconds, _cancellation.Token);
                        continue;
                    }

                    // Process each message
                    foreach (var message in streamResults)
                    {
                        if (_cancellation.Token.IsCancellationRequested)
                            break;

                        await ProcessMessage(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when shutting down
                    break;
                }
                catch (RedisServerException ex) when (
                    ex.Message.Contains("NOGROUP", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
                {
                    if (_transport.AutoProvision)
                    {
                        _logger.LogWarning(ex, "Consumer group or stream missing for {StreamKey}/{Group}. Attempting to create and retry.", _endpoint.StreamKey, _endpoint.ConsumerGroup);
                        await EnsureGroupExistsAsync(database);
                        await Task.Delay(TimeSpan.FromMilliseconds(200), _cancellation.Token);
                    }
                    else
                    {
                        _logger.LogError(ex, "Redis stream/consumer group missing for {StreamKey}/{Group}, and AutoProvision is disabled. Enable AutoProvision() or run AddResourceSetupOnStartup() to create resources.", _endpoint.StreamKey, _endpoint.ConsumerGroup);
                        _status = ListeningStatus.Stopped;
                        _cancellation.Cancel();
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Redis stream consumer loop for {StreamKey}", _endpoint.StreamKey);

                    // Brief delay before retrying to avoid tight error loops
                    await Task.Delay(TimeSpan.FromSeconds(5), _cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in Redis stream consumer loop for {StreamKey}", _endpoint.StreamKey);
        }

        _logger.LogDebug("Redis stream consumer loop ended for {StreamKey}", _endpoint.StreamKey);
    }

    private async Task ProcessMessage(StreamEntry streamEntry)
    {
        try
        {
            // Convert Redis stream message to Wolverine envelope using mapper
            _endpoint.EnvelopeMapper ??= _endpoint.BuildMapper(_runtime);

            var envelope = new Envelope { TopicName = _endpoint.StreamKey };
            _endpoint.EnvelopeMapper.MapIncomingToEnvelope(envelope, streamEntry);

            _logger.LogDebug("Received message {EnvelopeId} from Redis stream {StreamKey} (stream message ID: {StreamMessageId})",
                envelope.Id, _endpoint.StreamKey, streamEntry.Id);

            // Send to Wolverine for processing (this will invoke continuations that may call Complete/Defer)
            await _receiver.ReceivedAsync(this, envelope);

            // Do not ACK here; CompleteAsync/DeferAsync will handle ACK or requeue as appropriate
            _logger.LogDebug("Processed message {EnvelopeId} from Redis stream {StreamKey}", envelope.Id, _endpoint.StreamKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process message {MessageId} from Redis stream {StreamKey}",
                streamEntry.Id, _endpoint.StreamKey);

            // Note: We don't acknowledge failed messages, so they can be retried later
            // Redis will automatically retry unacknowledged messages based on the consumer group configuration
        }
    }


    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _consumerTask.SafeDispose();
        _scheduledTask.SafeDispose();
        _cancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task LookForScheduledMessagesAsync()
    {
        // Add randomness to prevent all nodes from polling at the exact same time
        await Task.Delay(_settings.ScheduledJobFirstExecution);

        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var count = await MoveScheduledToReadyStreamAsync(_cancellation.Token);
                if (count > 0)
                {
                    _logger.LogInformation("Propagated {Number} scheduled messages to Redis stream {StreamKey}", count, _endpoint.StreamKey);
                }

                await DeleteExpiredAsync(CancellationToken.None);

                failedCount = 0;

                await Task.Delay(_settings.ScheduledJobPollingTime);
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                _logger.LogError(e, "Error while trying to propagate scheduled messages from Redis stream {StreamKey}",
                    _endpoint.StreamKey);

                await Task.Delay(pauseTime);
            }
        }
    }

    public async Task<long> MoveScheduledToReadyStreamAsync(CancellationToken cancellationToken)
    {
        var database = _transport.GetDatabase(database: _endpoint.DatabaseId);
        var scheduledKey = _endpoint.ScheduledMessagesKey;

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Query only entries whose score <= now (ready to execute), without
            // removing entries that aren't ready yet. This avoids the race condition
            // where a pop-then-re-add temporarily empties the sorted set.
            var readyEntries = await database.SortedSetRangeByScoreAsync(
                scheduledKey,
                double.NegativeInfinity,
                now,
                take: 100,
                order: Order.Ascending);

            if (readyEntries.Length == 0)
            {
                return 0;
            }

            _logger.LogDebug(
                "Found {Count} scheduled messages ready for execution in {ScheduledKey}",
                readyEntries.Length,
                scheduledKey);

            long count = 0;

            foreach (var serializedEnvelope in readyEntries)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    // Remove from sorted set first; if another consumer already
                    // removed it we skip.
                    var removed = await database.SortedSetRemoveAsync(scheduledKey, serializedEnvelope);
                    if (!removed) continue;

                    // Deserialize the envelope
                    var envelope = EnvelopeSerializer.Deserialize(serializedEnvelope);

                    // Add it to the stream
                    _endpoint.EnvelopeMapper ??= _endpoint.BuildMapper(_runtime);
                    var fields = new List<NameValueEntry>();
                    _endpoint.EnvelopeMapper.MapEnvelopeToOutgoing(envelope, fields);

                    var messageId = await database.StreamAddAsync(
                        _endpoint.StreamKey,
                        fields.ToArray());

                    count++;

                    _logger.LogDebug(
                        "Moved scheduled message {EnvelopeId} (Attempts={Attempts}) to stream {StreamKey} with message ID {MessageId}",
                        envelope.Id,
                        envelope.Attempts,
                        _endpoint.StreamKey,
                        messageId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error processing scheduled message in {ScheduledKey}",
                        scheduledKey);
                    // Remove the corrupted message from the scheduled set
                    await database.SortedSetRemoveAsync(scheduledKey, serializedEnvelope);
                }
            }

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving scheduled messages from {ScheduledKey} to stream {StreamKey}",
                scheduledKey, _endpoint.StreamKey);
            throw;
        }
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        try
        {
            var database = _transport.GetDatabase(database: _endpoint.DatabaseId);
            var scheduledKey = _endpoint.ScheduledMessagesKey;
            
            // Get all messages from the scheduled set to check their expiration
            var allMessages = await database.SortedSetRangeByScoreAsync(scheduledKey);
            
            if (allMessages == null || allMessages.Length == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var expiredCount = 0;

            foreach (var serializedEnvelope in allMessages)
            {
                try
                {
                    var envelope = EnvelopeSerializer.Deserialize(serializedEnvelope);
                    
                    // Check if the message has expired
                    if (envelope.DeliverBy.HasValue && envelope.DeliverBy.Value <= now)
                    {
                        await database.SortedSetRemoveAsync(scheduledKey, serializedEnvelope);
                        expiredCount++;
                        
                        _logger.LogDebug("Deleted expired scheduled message {EnvelopeId} from {ScheduledKey}", 
                            envelope.Id, scheduledKey);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking expiration for scheduled message in {ScheduledKey}, removing it", scheduledKey);
                    // Remove corrupted messages
                    await database.SortedSetRemoveAsync(scheduledKey, serializedEnvelope);
                    expiredCount++;
                }
            }

            if (expiredCount > 0)
            {
                _logger.LogInformation("Deleted {Count} expired scheduled messages from {ScheduledKey}", 
                    expiredCount, scheduledKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting expired messages from {ScheduledKey}", _endpoint.ScheduledMessagesKey);
        }
    }
}
