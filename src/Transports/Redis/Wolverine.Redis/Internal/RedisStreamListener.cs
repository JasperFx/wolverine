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

public class RedisStreamListener : IListener, ISupportDeadLetterQueue, IReportConnectionState, IReportReceiveLoopHealth,
    ISupportNativeScheduling
{
    private readonly RedisTransport _transport;
    private readonly RedisStreamEndpoint _endpoint;
    private readonly IWolverineRuntime _runtime;
    private readonly IReceiver _receiver;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DurabilitySettings _settings;

    // GH-3236: the main XREADGROUP/XAUTOCLAIM consumer loop now runs on the shared BackgroundReceiveLoop, which
    // owns the task, backoff, idle delay, heartbeat, and teardown. _autoClaimWatch is cross-iteration timing state
    // (when to switch a poll to XAUTOCLAIM), promoted to a field now that the loop body is per-iteration.
    private BackgroundReceiveLoop? _loop;
    private readonly Stopwatch _autoClaimWatch = Stopwatch.StartNew();
    private Task? _scheduledTask;
    private ListeningStatus _status = ListeningStatus.Stopped;
    private string _consumerName;

    // When non-null, this listener consumes over a tenant's dedicated multiplexer (broker-per-tenant)
    // instead of the transport's shared connection. GH-3309.
    private readonly IConnectionMultiplexer? _connection;

    public RedisStreamListener(RedisTransport transport, RedisStreamEndpoint endpoint,
        IWolverineRuntime runtime, IReceiver receiver, IConnectionMultiplexer? connection = null)
    {
        _transport = transport;
        _endpoint = endpoint;
        _runtime = runtime;
        _receiver = receiver;
        _connection = connection;
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

    // GH-3231: surface the StackExchange.Redis multiplexer connection state. The XREADGROUP poll loop runs over a
    // long-lived, auto-reconnecting multiplexer, so this lets external monitors see a listener whose multiplexer is
    // down even though ListeningStatus still reports Accepting.
    public TransportConnectionState ConnectionState =>
        getDatabase().Multiplexer.IsConnected
            ? TransportConnectionState.Connected
            : TransportConnectionState.Disconnected;

    private IDatabase getDatabase() =>
        _connection?.GetDatabase(_endpoint.DatabaseId) ?? _transport.GetDatabase(database: _endpoint.DatabaseId);

    // GH-3236: surface the consumer loop's liveness (heartbeat + faulted/hung detection) for EndpointHealthSnapshot.
    public ReceiveLoopStatus ReceiveLoopStatus => _loop?.ReceiveLoopStatus ?? ReceiveLoopStatus.NotStarted;
    public DateTimeOffset? LastReceiveLoopActivityAt => _loop?.LastReceiveLoopActivityAt;

    internal bool DeleteOnAck => _transport.DeleteStreamEntryOnAck;

    /// <summary>
    ///     GH-4058. <c>DeleteStreamEntryOnAck(true)</c> settles with <c>XACKDEL</c>, which arrived in Redis 8.2.
    ///     An older server answers <c>ERR unknown command 'XACKDEL'</c> to every single ack, so the entries pile
    ///     up in the consumer group's pending list forever: under auto-claim they are redelivered and reprocessed
    ///     without end, and without it they leak. There is no honest fallback -- <c>XACK</c> plus <c>XDEL</c> is
    ///     not the same operation, because <c>XDEL</c> drops the entry for every consumer group on the stream
    ///     rather than only this one, which is precisely why <c>XACKDEL</c> exists. So refuse to start instead.
    /// </summary>
    private async Task assertDeleteOnAckIsSupportedAsync()
    {
        if (!DeleteOnAck) return;

        var supported = await RedisStreamCapabilities.SupportsXackDelAsync(getDatabase());

        if (supported == false)
        {
            throw new InvalidOperationException(
                $"The Redis transport is configured with DeleteStreamEntryOnAck(true), but the Redis server behind stream '{_endpoint.StreamKey}' (db {_endpoint.DatabaseId}) does not support the {RedisStreamCapabilities.XackDel} command that setting requires. {RedisStreamCapabilities.XackDel} was added in Redis 8.2; on an older server every acknowledgement fails and no message on this listener is ever settled. Upgrade the server to Redis 8.2 or later, or call DeleteStreamEntryOnAck(false) to acknowledge with XACK and leave the entries in the stream. Wolverine deliberately does not fall back to XACK plus XDEL, because XDEL removes the entry for every consumer group on the stream rather than only this one.");
        }

        if (supported == null)
        {
            _logger.LogWarning(
                "Unable to confirm that the Redis server behind stream {StreamKey} (db {DatabaseId}) supports the {Command} command required by DeleteStreamEntryOnAck(true). Starting anyway, but acknowledgements will fail at runtime if the server is older than Redis 8.2.",
                _endpoint.StreamKey, _endpoint.DatabaseId, RedisStreamCapabilities.XackDel);
        }
    }

    /// <summary>
    ///     The one place that settles a stream entry for this consumer group, so the <c>DeleteOnAck</c> choice
    ///     cannot drift between the complete, requeue, and dead-letter paths.
    /// </summary>
    private Task acknowledgeAsync(IDatabase db, string entryId)
    {
        return DeleteOnAck
            ? db.StreamAcknowledgeAndDeleteAsync(_endpoint.StreamKey, _endpoint.ConsumerGroup!,
                StreamTrimMode.Acknowledged, entryId)
            : db.StreamAcknowledgeAsync(_endpoint.StreamKey, _endpoint.ConsumerGroup!, entryId);
    }

    /// <summary>
    ///     GH-4058. A one-off ack failure is a warning -- the entry stays pending and gets redelivered. Redis
    ///     rejecting the command as unknown is not: it means nothing on this listener is being acknowledged, and
    ///     nothing ever will be, so it is logged at Error with the fix in the message.
    /// </summary>
    private void logAckFailure(Exception ex, Envelope envelope, string context)
    {
        if (RedisStreamCapabilities.IsUnknownCommandFailure(ex))
        {
            _logger.LogError(ex,
                "Redis rejected the acknowledgement command for stream {StreamKey} (db {DatabaseId}) as unknown while {Context} envelope {EnvelopeId}. No message on this listener is being acknowledged, and entries will accumulate in the pending list of consumer group {ConsumerGroup} indefinitely. DeleteStreamEntryOnAck(true) requires {Command}, which needs Redis 8.2 or later; upgrade the server or call DeleteStreamEntryOnAck(false).",
                _endpoint.StreamKey, _endpoint.DatabaseId, context, envelope.Id, _endpoint.ConsumerGroup,
                RedisStreamCapabilities.XackDel);
        }
        else
        {
            _logger.LogWarning(ex,
                "Error ACKing Redis stream message on {StreamKey} while {Context} envelope {EnvelopeId}",
                _endpoint.StreamKey, context, envelope.Id);
        }
    }

    // ISupportDeadLetterQueue implementation
    public bool NativeDeadLetterQueueEnabled => _endpoint.NativeDeadLetterQueueEnabled;
    
    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        try
        {
            var database = getDatabase();
            
            // Stamp the standard failure metadata (GH-3474) so the serialized envelope
            // round-trips it, then serialize
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
            var serializedEnvelope = EnvelopeSerializer.Serialize(envelope);

            // Build the dead letter entry with error information
            var fields = new List<NameValueEntry>
            {
                new("envelope", serializedEnvelope),
                new(DeadLetterQueueConstants.ExceptionTypeHeader, exception.GetType().FullName ?? "Unknown"),
                new(DeadLetterQueueConstants.ExceptionMessageHeader, exception.Message ?? ""),
                new(DeadLetterQueueConstants.ExceptionStackHeader, DeadLetterQueueConstants.TruncateStackTrace(exception.StackTrace)),
                new(DeadLetterQueueConstants.FailedAtHeader, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()),
                new("message-type", envelope.MessageType ?? "Unknown"),
                new("envelope-id", envelope.Id.ToString()),
                new("attempts", envelope.Attempts.ToString())
            };

            if (envelope.Destination != null)
            {
                fields.Add(new(DeadLetterQueueConstants.OriginalDestinationHeader, envelope.Destination.ToString()));
            }
            
            // Add the dead letter entry to the dead letter stream
            var deadLetterMessageId = await database.StreamAddAsync(_endpoint.DeadLetterQueueKey, fields.ToArray());
            
            _logger.LogInformation("Moved envelope {EnvelopeId} to dead letter queue {DeadLetterKey} with message ID {DeadLetterMessageId}. Exception: {ExceptionType}: {ExceptionMessage}",
                envelope.Id, _endpoint.DeadLetterQueueKey, deadLetterMessageId, exception.GetType().Name, exception.Message);
            
            // Acknowledge the original message if we have the Redis entry ID
            if (envelope.Headers.TryGetValue(RedisEnvelopeMapper.RedisEntryIdHeader, out var idString) && !string.IsNullOrEmpty(idString))
            {
                try
                {
                    await acknowledgeAsync(database, idString!);
                }
                catch (Exception ackEx)
                {
                    logAckFailure(ackEx, envelope, "acknowledging after a move to the dead letter queue");
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
        // GH-4058: reject a DeleteStreamEntryOnAck(true) that this server cannot honor before we provision
        // anything. Left unchecked it is a total, silent failure -- every settle throws and nothing is ever
        // acknowledged -- so it is not something to discover one warning at a time in production.
        await assertDeleteOnAckIsSupportedAsync();

        // Only create resources at listener init time if AutoProvision is enabled. Provision the consumer
        // group over THIS listener's connection so a tenant listener creates its group on the tenant's own
        // server (broker-per-tenant), not the shared one. GH-3309.
        if (_transport.AutoProvision)
        {
            await EnsureGroupExistsAsync(getDatabase());
        }
        else
        {
            // Fail-fast if the required consumer group (or stream) is missing when AutoProvision is disabled.
            if (_endpoint.IsListener && !string.IsNullOrEmpty(_endpoint.ConsumerGroup))
            {
                try
                {
                    var db = getDatabase();
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

            // Start the consumer loop on the shared BackgroundReceiveLoop. The idle delay is the endpoint's
            // BlockTimeout, matching the previous "no messages -> wait BlockTimeout before polling again" behavior.
            _autoClaimWatch.Restart();
            _loop = new BackgroundReceiveLoop(Address, _logger, pollOnceAsync, _cancellation.Token,
                _endpoint.BlockTimeoutMilliseconds.Milliseconds());
            _loop.Start();

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
        await _cancellation.CancelAsync();

        if (_loop != null)
        {
            await _loop.StopAsync(TimeSpan.FromSeconds(10));
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

            await acknowledgeAsync(getDatabase(), idString!);
            _logger.LogDebug("Acknowledged Redis stream message {StreamId} on {StreamKey}", idString, _endpoint.StreamKey);
        }
        catch (Exception ex)
        {
            logAckFailure(ex, envelope, "completing");
        }
    }

    /// <summary>
    ///     GH-4028. Redis-native scheduled retries for the non-durable modes: the message is parked in the
    ///     stream's scheduled sorted set and the polling loop puts it back on the stream when it is due. A
    ///     Durable listener deliberately reports <c>false</c> here so the scheduled retry goes through the
    ///     inbox like every other durable transport -- a listener-level reschedule bypasses the inbox
    ///     entirely, and re-adding the same envelope id to the stream would collide with the inbox row on
    ///     redelivery and be discarded as a duplicate.
    /// </summary>
    public bool NativeSchedulingEnabled => _endpoint.Mode != EndpointMode.Durable;

    public async Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        envelope.ScheduledTime = time;

        // Park the copy first, then settle the original: a crash in between costs a duplicate, not a loss
        await _endpoint.ScheduleRetryAsync(envelope, _cancellation.Token);
        await CompleteAsync(envelope);
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        try
        {
            var db = getDatabase();

            // 1) Ack the current pending entry if we can
            if (envelope.Headers.TryGetValue(RedisEnvelopeMapper.RedisEntryIdHeader, out var idString) && !string.IsNullOrEmpty(idString))
            {
                try
                {
                    await acknowledgeAsync(db, idString!);
                }
                catch (Exception ackEx)
                {
                    logAckFailure(ackEx, envelope, "acknowledging before a requeue");
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

    // One iteration of the consumer loop, driven by BackgroundReceiveLoop. Returns true when it read+processed
    // entries (loop continues immediately), false when idle (loop applies the BlockTimeout idle delay). The NOGROUP
    // handling is kept here: provision-and-retry when AutoProvision is on, otherwise stop the listener — the same
    // fail-fast behavior as before. Other exceptions propagate to BackgroundReceiveLoop's log-and-backoff policy.
    private async Task<bool> pollOnceAsync(CancellationToken token)
    {
        var database = getDatabase();

        try
        {
            // Determine if it's time to use AutoClaim instead of regular read
            var shouldUseAutoClaim = _endpoint.AutoClaimEnabled &&
                                     _autoClaimWatch.Elapsed >= _endpoint.AutoClaimPeriod;

            // Read from either XREADGROUP or XAUTOCLAIM
            var streamResults = await ReadEntriesAsync(database, shouldUseAutoClaim);

            if (shouldUseAutoClaim)
            {
                _autoClaimWatch.Restart();
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
                // No messages — the loop applies its idle delay (BlockTimeout) before polling again.
                return false;
            }

            // Process each message
            foreach (var message in streamResults)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                await ProcessMessage(message);
            }

            return true;
        }
        catch (RedisServerException ex) when (
            ex.Message.Contains("NOGROUP", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            if (_transport.AutoProvision)
            {
                _logger.LogWarning(ex, "Consumer group or stream missing for {StreamKey}/{Group}. Attempting to create and retry.", _endpoint.StreamKey, _endpoint.ConsumerGroup);
                await EnsureGroupExistsAsync(database);
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                return false;
            }

            _logger.LogError(ex, "Redis stream/consumer group missing for {StreamKey}/{Group}, and AutoProvision is disabled. Enable AutoProvision() or run AddResourceSetupOnStartup() to create resources.", _endpoint.StreamKey, _endpoint.ConsumerGroup);
            _status = ListeningStatus.Stopped;
            // Cancel the shared token so the BackgroundReceiveLoop stops (no point retrying a misconfiguration).
            await _cancellation.CancelAsync();
            return false;
        }
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


    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        if (_loop != null)
        {
            await _loop.DisposeAsync();
        }
        _scheduledTask.SafeDispose();
        _cancellation.Dispose();
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
        var database = getDatabase();
        var scheduledKey = _endpoint.ScheduledMessagesKey;

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Query only entries whose score <= now (ready to execute), without
            // removing entries that aren't ready yet. This avoids the race condition
            // where a pop-then-re-add temporarily empties the sorted set.
            // WithScores so that an entry we claim but then fail to hand off to the stream can be
            // restored at its original due time instead of being dropped (GH-3613).
            var readyEntries = await database.SortedSetRangeByScoreWithScoresAsync(
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

            foreach (var entry in readyEntries)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var serializedEnvelope = entry.Element;
                var claimed = false;

                try
                {
                    // Deserialize BEFORE claiming the entry. Claiming first meant that a payload we
                    // could not read was already gone from the sorted set by the time the catch below
                    // ran, so the message was lost outright (GH-3613).
                    var envelope = EnvelopeSerializer.Deserialize(serializedEnvelope!);

                    // Claim the entry; if another consumer already removed it we skip.
                    claimed = await database.SortedSetRemoveAsync(scheduledKey, serializedEnvelope);
                    if (!claimed) continue;

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
                        "Error processing scheduled message in {ScheduledKey}, leaving it in place to be retried",
                        scheduledKey);

                    // If we already claimed the entry but failed to hand it to the stream, put it back
                    // rather than dropping it. An entry we never claimed is left where it is.
                    if (claimed)
                    {
                        try
                        {
                            await database.SortedSetAddAsync(scheduledKey, serializedEnvelope, entry.Score);
                        }
                        catch (Exception restoreFailure)
                        {
                            _logger.LogError(restoreFailure,
                                "Unable to restore scheduled message to {ScheduledKey} after a failed hand-off to stream {StreamKey}; the message is lost",
                                scheduledKey, _endpoint.StreamKey);
                        }
                    }
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


    /// <summary>
    ///     A scheduled entry that cannot be deserialized. Counts consecutive read failures per entry in a
    ///     side hash and, once <see cref="RedisTransport.MaxScheduledReadAttempts" /> is reached, moves the
    ///     raw bytes to the dead letter stream and clears them from the scheduled set.
    ///
    ///     <para>The entry is never simply dropped: with dead-lettering disabled (either
    ///     <c>MaxScheduledReadAttempts = 0</c> or an endpoint whose native DLQ is off) it stays in the sorted
    ///     set exactly as GH-3613 left it. See GH-3644.</para>
    /// </summary>
    private async Task handleUnreadableScheduledEntryAsync(IDatabase database, string scheduledKey,
        RedisValue serializedEnvelope, Exception failure)
    {
        var limit = _transport.MaxScheduledReadAttempts;

        if (limit <= 0 || !_endpoint.NativeDeadLetterQueueEnabled)
        {
            _logger.LogWarning(failure,
                "Unable to read a scheduled message in {ScheduledKey} while checking expiration; leaving it in place",
                scheduledKey);
            return;
        }

        var failureKey = _endpoint.UnreadableScheduledMessagesKey;

        long attempts;
        try
        {
            // The payload IS the sorted-set member, so it doubles as a stable hash field. The hash is given
            // the same lifetime bound as a long-lived scheduled entry so a transient blip cannot leak keys.
            attempts = await database.HashIncrementAsync(failureKey, serializedEnvelope);
            await database.KeyExpireAsync(failureKey, TimeSpan.FromDays(7));
        }
        catch (Exception counterFailure)
        {
            // Failing to count must not escalate into losing the message.
            _logger.LogWarning(counterFailure,
                "Unable to record a read failure for a scheduled message in {ScheduledKey}; leaving it in place",
                scheduledKey);
            return;
        }

        if (attempts < limit)
        {
            _logger.LogWarning(failure,
                "Unable to read a scheduled message in {ScheduledKey} while checking expiration (attempt {Attempts} of {Limit}); leaving it in place",
                scheduledKey, attempts, limit);
            return;
        }

        // No Envelope was ever produced, so the usual DeadLetterQueueConstants.StampFailureMetadata path is
        // unavailable. Write the raw payload plus what diagnostics we do have, so an operator can recover
        // the bytes by hand.
        var fields = new List<NameValueEntry>
        {
            new("envelope", serializedEnvelope),
            new(DeadLetterQueueConstants.ExceptionTypeHeader, failure.GetType().FullName ?? "Unknown"),
            new(DeadLetterQueueConstants.ExceptionMessageHeader, failure.Message ?? ""),
            new(DeadLetterQueueConstants.ExceptionStackHeader,
                DeadLetterQueueConstants.TruncateStackTrace(failure.StackTrace)),
            new(DeadLetterQueueConstants.FailedAtHeader,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()),
            new(DeadLetterQueueConstants.OriginalDestinationHeader, _endpoint.Uri.ToString()),
            new("message-type", "Unknown"),
            new("unreadable-scheduled-message", "true"),
            new("read-attempts", attempts.ToString())
        };

        try
        {
            var messageId = await database.StreamAddAsync(_endpoint.DeadLetterQueueKey, fields.ToArray());

            // Only stop tracking it once it is safely in the dead letter stream.
            await database.SortedSetRemoveAsync(scheduledKey, serializedEnvelope);
            await database.HashDeleteAsync(failureKey, serializedEnvelope);

            _logger.LogError(failure,
                "A scheduled message in {ScheduledKey} could not be read after {Attempts} attempts and was moved to the dead letter queue {DeadLetterKey} with message ID {MessageId}",
                scheduledKey, attempts, _endpoint.DeadLetterQueueKey, messageId);
        }
        catch (Exception deadLetterFailure)
        {
            _logger.LogError(deadLetterFailure,
                "Unable to move an unreadable scheduled message from {ScheduledKey} to the dead letter queue {DeadLetterKey}; leaving it in place",
                scheduledKey, _endpoint.DeadLetterQueueKey);
        }
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        try
        {
            var database = getDatabase();
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
                    var envelope = EnvelopeSerializer.Deserialize(serializedEnvelope!);

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
                    // Never delete outright. This sweep only exists to evict messages that are genuinely
                    // past their DeliverBy; a message we cannot read is not known to be expired, and
                    // deleting it here silently destroyed scheduled retries that never got a chance to
                    // redeliver or to reach the dead letter queue (GH-3613). But leaving it forever means it
                    // re-warns on every sweep and can never be cleared, so an entry that fails to read
                    // repeatedly is dead-lettered instead (GH-3644).
                    await handleUnreadableScheduledEntryAsync(database, scheduledKey, serializedEnvelope, ex);
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
