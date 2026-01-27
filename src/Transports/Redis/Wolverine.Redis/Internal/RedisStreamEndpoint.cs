using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.Redis;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Redis.Internal;

public class RedisStreamEndpoint : Endpoint<IRedisEnvelopeMapper, RedisEnvelopeMapper>, IBrokerEndpoint, IBrokerQueue, IDatabaseBackedEndpoint
{
    private readonly RedisTransport _transport;

    internal bool SupportsNativeScheduledSend { get; set; } = true;
    
    internal RedisStreamEndpoint(Uri uri, RedisTransport transport, EndpointRole role = EndpointRole.Application) 
        : base(uri, role)
    {
        _transport = transport;
        var (streamKey, databaseId) = ParseStreamKeyAndDatabase(uri);
        StreamKey = streamKey;
        DatabaseId = databaseId;
        ConsumerGroup = ParseConsumerGroup(uri);
        EndpointName = StreamKey;
        
        // Redis Streams work well in buffered mode by default
        Mode = EndpointMode.BufferedInMemory;
    }

    /// <summary>
    /// The Redis Stream key name
    /// </summary>
    public string StreamKey { get; }
    
    /// <summary>
    /// The Redis database ID (0-15 for standard Redis)
    /// </summary>
    public int DatabaseId { get; }
    
    /// <summary>
    /// The Redis Sorted Set key for scheduled messages
    /// </summary>
    public string ScheduledMessagesKey => $"{StreamKey}:scheduled";
    
    /// <summary>
    /// The Redis Stream key for dead letter messages
    /// </summary>
    public string DeadLetterQueueKey => $"{StreamKey}:dead-letter";
    
    /// <summary>
    /// Enable native dead letter queue support for this endpoint
    /// </summary>
    public bool NativeDeadLetterQueueEnabled { get; set; } = true;
    
    /// <summary>
    /// The consumer group name for this endpoint (if listening)
    /// </summary>
    public string? ConsumerGroup { get; set; }
    
    /// <summary>
    /// Maximum number of messages to read in a single batch from Redis Stream
    /// </summary>
    public int BatchSize { get; set; } = 10;
    
    /// <summary>
    /// Consumer name within the consumer group. Defaults to machine name + process ID
    /// </summary>
    public string? ConsumerName { get; set; }
    
    /// <summary>
    /// Block timeout in milliseconds when reading from streams
    /// </summary>
    public int BlockTimeoutMilliseconds { get; set; } = 1000;

    /// <summary>
    /// If true, purge the stream on startup. Useful for tests.
    /// </summary>
    public bool PurgeOnStartup { get; set; }

    /// <summary>
    /// Enable periodic auto-claiming of pending messages within the main consumer loop
    /// </summary>
    public bool AutoClaimEnabled { get; set; } = false;

    /// <summary>
    /// Period between auto-claim attempts when integrated into the consumer loop (default: 30 seconds)
    /// </summary>
    public TimeSpan AutoClaimPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum idle time before claiming pending messages for auto-claim (default: 1 min)
    /// </summary>
    public TimeSpan AutoClaimMinIdle { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Determines where to start consuming when creating a new consumer group.
    /// NewMessages (default): only consume messages added after group creation
    /// Beginning: consume from the start of the stream including existing messages
    /// </summary>
    public StartFrom StartFrom { get; set; } = StartFrom.NewMessages;

    private static (string streamKey, int databaseId) ParseStreamKeyAndDatabase(Uri uri)
    {
        // Only support the new format: redis://stream/{dbId}/{streamKey}
        if (!uri.Host.Equals("stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Redis URI must use the format 'redis://stream/{{dbId}}/{{streamKey}}': {uri}");
        }

        if (uri.Segments.Length < 3)
        {
            throw new ArgumentException($"Redis URI must specify both database ID and stream key in format 'redis://stream/{{dbId}}/{{streamKey}}': {uri}");
        }

        var databaseSegment = uri.Segments[1].TrimEnd('/');
        var streamKeySegment = uri.Segments[2].TrimEnd('/');

        if (!int.TryParse(databaseSegment, out var databaseId) || databaseId < 0)
        {
            throw new ArgumentException($"Database ID must be a non-negative integer in Redis URI: {uri}");
        }

        if (string.IsNullOrEmpty(streamKeySegment))
        {
            throw new ArgumentException($"Stream key cannot be empty in Redis URI: {uri}");
        }

        return (streamKeySegment, databaseId);
    }
    
    private static string? ParseConsumerGroup(Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return query["consumerGroup"];
    }

    protected override RedisEnvelopeMapper buildMapper(IWolverineRuntime runtime)
    {
        return new RedisEnvelopeMapper(this);
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        if (string.IsNullOrEmpty(ConsumerGroup))
        {
            throw new InvalidOperationException($"Consumer group is required for listening to Redis stream '{StreamKey}'");
        }

        var listener = new RedisStreamListener(_transport, this, runtime, receiver);
        await listener.InitializeAsync();
        return listener;
    }
    
    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        if (NativeDeadLetterQueueEnabled)
        {
            var deadLetterEndpoint = _transport.StreamEndpoint(DeadLetterQueueKey);
            deadLetterSender = new InlineRedisStreamSender(
                _transport,
                deadLetterEndpoint,
                runtime);
            return true;
        }

        deadLetterSender = null;
        return false;
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        // Honor AutoProvision/AutoPurge semantics similar to other transports
        try
        {
            var db = _transport.GetDatabase(database: DatabaseId);

            if (_transport.AutoProvision)
            {
                // Ensure group exists with appropriate starting position
                if (!string.IsNullOrEmpty(ConsumerGroup) && IsListener)
                {
                    try
                    {
                        var startPosition = StartFrom == StartFrom.Beginning ? "0-0" : "$";
                        await db.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroup, startPosition, true);
                    }
                    catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
                    {
                        // already exists
                    }
                }
            }

            if (PurgeOnStartup || _transport.AutoPurgeAllQueues)
            {
                // Trim entire stream
                try { await PurgeAsync(logger); } catch { /* ignore */ }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error initializing Redis stream endpoint {Stream}", StreamKey);
            throw;
        }
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        EnvelopeMapper ??= BuildMapper(runtime);
        
        return Mode == EndpointMode.Inline
            ? new InlineRedisStreamSender(_transport, this, runtime)
            : new BatchedSender(this, new RedisSenderProtocol(_transport, this), runtime.Cancellation,
                runtime.LoggerFactory.CreateLogger<RedisSenderProtocol>());
    }

    public async ValueTask<bool> CheckAsync()
    {
        try
        {
            var database = _transport.GetDatabase(database: DatabaseId);
            // Check if stream exists by getting stream info
            var info = await database.StreamInfoAsync(StreamKey);
            return true;
        }
        catch (RedisServerException ex) when (ex.Message.Contains("no such key"))
        {
            // Stream doesn't exist yet, which is fine
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        if (Role == EndpointRole.System)
        {
            return; // Don't tear down system endpoints
        }
        
        try
        {
            var database = _transport.GetDatabase(database: DatabaseId);
            
            // Delete consumer group if this endpoint created it
            if (!string.IsNullOrEmpty(ConsumerGroup))
            {
                logger.LogInformation("Removing consumer group {ConsumerGroup} from Redis stream {StreamKey}", 
                    ConsumerGroup, StreamKey);
                await database.StreamDeleteConsumerGroupAsync(StreamKey, ConsumerGroup);
            }
            
            logger.LogDebug("Teardown completed for Redis stream endpoint {StreamKey}", StreamKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to teardown Redis stream endpoint {StreamKey}", StreamKey);
        }
    }

    // IBrokerQueue implementation for diagnostics and purge support
    public async ValueTask PurgeAsync(ILogger logger)
    {
        try
        {
            var db = _transport.GetDatabase(database: DatabaseId);
            if (await db.KeyDeleteAsync(StreamKey))
                logger.LogInformation("Purged Redis stream {StreamKey}", StreamKey);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error purging Redis stream {StreamKey}", StreamKey);
            throw;
        }
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var dict = new Dictionary<string, string>
        {
            { "streamKey", StreamKey }
        };

        try
        {
            var db = _transport.GetDatabase(database: DatabaseId);
            var len = await db.StreamLengthAsync(StreamKey);
            dict["messageCount"] = len.ToString();
        }
        catch
        {
            // ignore
        }

        if (!string.IsNullOrEmpty(ConsumerGroup))
        {
            dict["consumerGroup"] = ConsumerGroup!;
        }

        return dict;
    }
    
    public async ValueTask SetupAsync(ILogger logger)
    {
        try
        {
            var database = _transport.GetDatabase(database: DatabaseId);
            
            // Create consumer group if specified and this is a listener
            if (!string.IsNullOrEmpty(ConsumerGroup) && IsListener)
            {
                try
                {
                    var startPosition = StartFrom == StartFrom.Beginning ? "0-0" : "$";
                    var positionDescription = StartFrom == StartFrom.Beginning ? "beginning" : "end";
                    
                    logger.LogDebug("Creating consumer group {ConsumerGroup} for Redis stream {StreamKey} starting from {Position}", 
                        ConsumerGroup, StreamKey, positionDescription);
                    
                    await database.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroup, startPosition, true);
                    
                    logger.LogInformation("Created consumer group {ConsumerGroup} for Redis stream {StreamKey} starting from {Position}", 
                        ConsumerGroup, StreamKey, positionDescription);
                }
                catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
                {
                    // Group already exists, which is fine
                    logger.LogDebug("Consumer group {ConsumerGroup} already exists for stream {StreamKey}", 
                        ConsumerGroup, StreamKey);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to setup Redis stream endpoint {StreamKey}", StreamKey);
            throw;
        }
    }

    // IDatabaseBackedEndpoint implementation
    public async Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellation)
    {
        try
        {
            var database = _transport.GetDatabase(database: DatabaseId);
            
            // Note: envelope.Attempts has already been incremented by Executor.ExecuteAsync
            // before this method is called, so we don't increment it here
            
            // CRITICAL FIX: Ensure envelope.Data is populated before serialization
            // EnvelopeSerializer requires Data to be non-null to serialize all properties correctly
            if (envelope.Data == null && envelope.Message != null)
            {
                // Accessing the Data property will trigger serialization of the Message
                _ = envelope.Data;
            }
            
            envelope.Headers["attempts"] = envelope.Attempts.ToString();
            // Serialize the envelope for storage - this includes ALL properties including Attempts
            var serializedEnvelope = EnvelopeSerializer.Serialize(envelope);
            
            
            // Calculate the score (execution time) for the sorted set
            var scheduledTime = envelope.ScheduledTime ?? DateTimeOffset.UtcNow.AddSeconds(5); // Default 5 second retry delay
            var score = scheduledTime.ToUnixTimeMilliseconds();
            
            // Add the envelope to the scheduled messages sorted set
            await database.SortedSetAddAsync(ScheduledMessagesKey, serializedEnvelope, score);
            
            // Note: We don't need to add it to the stream yet - the polling mechanism in
            // RedisStreamListener will move it to the stream when it's due for execution
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to schedule retry for envelope {envelope.Id} on Redis stream {StreamKey}", ex);
        }
    }
}
