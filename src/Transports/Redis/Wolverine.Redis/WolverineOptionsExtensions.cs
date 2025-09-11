using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;

namespace Wolverine.Redis;

/// <summary>
/// Determines the starting position for new Redis Stream consumer groups
/// </summary>
public enum StartFrom
{
    /// <summary>
    /// Start consuming from the beginning of the stream (equivalent to "0-0")
    /// Similar to Kafka's AutoOffsetReset.Earliest
    /// </summary>
    Beginning,
    
    /// <summary>
    /// Start consuming only new messages added after group creation (equivalent to "$")
    /// Similar to Kafka's AutoOffsetReset.Latest
    /// </summary>
    NewMessages
}

public static class WolverineOptionsExtensions
{
    /// <summary>
    /// Adds Redis Streams transport to Wolverine with the specified connection string
    /// </summary>
    /// <param name="options">Wolverine configuration options</param>
    /// <param name="connectionString">Redis connection string (StackExchange.Redis format)</param>
    /// <returns>RedisTransport for fluent configuration</returns>
    public static RedisTransport UseRedisTransport(this WolverineOptions options, string connectionString)
    {
        var transport = new RedisTransport(connectionString);
        
        options.Transports.Add(transport);
        
        return transport;
    }

    /// <summary>
    /// Configure Wolverine to publish messages to the specified Redis stream (uses database 0)
    /// </summary>
    /// <param name="publishing">Publishing configuration</param>
    /// <param name="streamKey">Redis stream key name</param>
    /// <returns>Stream endpoint for further configuration</returns>
    public static RedisStreamEndpoint ToRedisStream(this IPublishToExpression publishing, string streamKey)
    {
        return publishing.ToRedisStream(streamKey, 0);
    }

    /// <summary>
    /// Configure Wolverine to publish messages to the specified Redis stream with database ID
    /// </summary>
    /// <param name="publishing">Publishing configuration</param>
    /// <param name="streamKey">Redis stream key name</param>
    /// <param name="databaseId">Redis database ID</param>
    /// <returns>Stream endpoint for further configuration</returns>
    public static RedisStreamEndpoint ToRedisStream(this IPublishToExpression publishing, string streamKey, int databaseId)
    {
        // Use correct pattern from Kafka transport
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<RedisTransport>();
        
        var endpoint = transport.StreamEndpoint(streamKey, databaseId);
        
        // Register this as a publishing destination
        publishing.To(endpoint.Uri);
        
        return endpoint;
    }

    /// <summary>
    /// Configure Wolverine to listen to messages from the specified Redis stream with a consumer group (uses database 0)
    /// </summary>
    /// <param name="options">Wolverine configuration options</param>
    /// <param name="streamKey">Redis stream key name</param>
    /// <param name="consumerGroup">Consumer group name</param>
    /// <returns>Stream endpoint for further configuration</returns>
    public static RedisStreamEndpoint ListenToRedisStream(this WolverineOptions options, string streamKey, string consumerGroup)
    {
        return options.ListenToRedisStream(streamKey, consumerGroup, 0);
    }

    /// <summary>
    /// Configure Wolverine to listen to a Redis stream with starting position control
    /// </summary>
    public static RedisStreamEndpoint ListenToRedisStream(this WolverineOptions options, string streamKey, string consumerGroup, StartFrom startFrom)
    {
        var endpoint = options.ListenToRedisStream(streamKey, consumerGroup, 0);
        endpoint.StartFrom = startFrom;
        return endpoint;
    }

    /// <summary>
    /// Configure Wolverine to listen to messages from the specified Redis stream with a consumer group and database ID
    /// </summary>
    /// <param name="options">Wolverine configuration options</param>
    /// <param name="streamKey">Redis stream key name</param>
    /// <param name="consumerGroup">Consumer group name</param>
    /// <param name="databaseId">Redis database ID</param>
    /// <returns>Stream endpoint for further configuration</returns>
    public static RedisStreamEndpoint ListenToRedisStream(this WolverineOptions options, string streamKey, string consumerGroup, int databaseId)
    {
        var transport = options.Transports.GetOrCreate<RedisTransport>();
        if (transport == null)
        {
            throw new InvalidOperationException("Redis transport has not been configured. Call UseRedisTransport() first.");
        }

        var endpoint = transport.StreamEndpoint(streamKey, databaseId, e =>
        {
            e.ConsumerGroup = consumerGroup;
            e.IsListener = true;
        });

        return endpoint;
    }

    /// <summary>
    /// Configure Wolverine to listen to messages from the specified Redis stream with a consumer group (uses database 0)
    /// </summary>
    /// <param name="options">Wolverine configuration options</param>
    /// <param name="streamKey">Redis stream key name</param>
    /// <param name="consumerGroup">Consumer group name</param>
    /// <param name="configure">Configuration action for the endpoint</param>
    /// <returns>Stream endpoint for further configuration</returns>
    public static RedisStreamEndpoint ListenToRedisStream(this WolverineOptions options, string streamKey, 
        string consumerGroup, Action<RedisStreamEndpoint> configure)
    {
        return options.ListenToRedisStream(streamKey, consumerGroup, 0, configure);
    }

    /// <summary>
    /// Configure Wolverine to listen to messages from the specified Redis stream with a consumer group and database ID
    /// </summary>
    /// <param name="options">Wolverine configuration options</param>
    /// <param name="streamKey">Redis stream key name</param>
    /// <param name="consumerGroup">Consumer group name</param>
    /// <param name="databaseId">Redis database ID</param>
    /// <param name="configure">Configuration action for the endpoint</param>
    /// <returns>Stream endpoint for further configuration</returns>
    public static RedisStreamEndpoint ListenToRedisStream(this WolverineOptions options, string streamKey, 
        string consumerGroup, int databaseId, Action<RedisStreamEndpoint> configure)
    {
        var endpoint = options.ListenToRedisStream(streamKey, consumerGroup, databaseId);
        configure(endpoint);
        return endpoint;
    }
}

/// <summary>
/// Extension methods for RedisTransport fluent configuration
/// </summary>
public static class RedisTransportExtensions
{
    /// <summary>
    /// Set the Redis database ID used for the per-node reply stream endpoint (request/reply mechanics)
    /// </summary>
    public static RedisTransport UseReplyStreamDatabase(this RedisTransport transport, int databaseId)
    {
        if (databaseId < 0) throw new ArgumentOutOfRangeException(nameof(databaseId));
        transport.ReplyDatabaseId = databaseId;
        return transport;
    }

    /// <summary>
    /// Enable auto-provisioning of Redis streams and consumer groups
    /// </summary>
    public static RedisTransport AutoProvision(this RedisTransport transport)
    {
        transport.AutoProvision = true;
        return transport;
    }

    /// <summary>
    /// Configure Redis transport to auto-purge streams on startup (useful for testing)
    /// </summary>
    public static RedisTransport AutoPurgeOnStartup(this RedisTransport transport)
    {
        transport.AutoPurgeAllQueues = true;
        return transport;
    }

    /// <summary>
    /// Configure a default consumer name selector applied to all Redis listeners
    /// when an endpoint-level ConsumerName is not explicitly set.
    /// Example: transport.ConfigureDefaultConsumerName((rt, ep) => $"{rt.Options.ServiceName}-{rt.DurabilitySettings.AssignedNodeNumber}");
    /// </summary>
    public static RedisTransport ConfigureDefaultConsumerName(this RedisTransport transport,
        Func<IWolverineRuntime, RedisStreamEndpoint, string> selector)
    {
        transport.DefaultConsumerNameSelector = selector;
        return transport;
    }
}

/// <summary>
/// Extension methods for RedisStreamEndpoint fluent configuration
/// </summary>
public static class RedisStreamEndpointExtensions
{
    /// <summary>
    /// Configure the batch size for reading messages from Redis streams
    /// </summary>
    public static RedisStreamEndpoint BatchSize(this RedisStreamEndpoint endpoint, int batchSize)
    {
        endpoint.BatchSize = batchSize;
        return endpoint;
    }

    /// <summary>
    /// Configure the consumer name for this endpoint
    /// </summary>
    public static RedisStreamEndpoint ConsumerName(this RedisStreamEndpoint endpoint, string consumerName)
    {
        endpoint.ConsumerName = consumerName;
        return endpoint;
    }

    /// <summary>
    /// Configure the block timeout when reading from Redis streams
    /// </summary>
    public static RedisStreamEndpoint BlockTimeout(this RedisStreamEndpoint endpoint, TimeSpan timeout)
    {
        endpoint.BlockTimeoutMilliseconds = (int)timeout.TotalMilliseconds;
        return endpoint;
    }

    /// <summary>
    /// Configure this endpoint to use buffered in-memory processing
    /// </summary>
    public static RedisStreamEndpoint BufferedInMemory(this RedisStreamEndpoint endpoint)
    {
        endpoint.Mode = EndpointMode.BufferedInMemory;
        return endpoint;
    }

    /// <summary>
    /// Configure this endpoint to use buffered in-memory processing
    /// </summary>
    public static RedisStreamEndpoint Sequential(this RedisStreamEndpoint endpoint)
    {
        endpoint.MaxDegreeOfParallelism = 1;
        return endpoint;
    }

    /// <summary>
    /// Configure this endpoint to use durable processing with message persistence
    /// </summary>
    public static RedisStreamEndpoint Durable(this RedisStreamEndpoint endpoint)
    {
        endpoint.Mode = EndpointMode.Durable;
        return endpoint;
    }

    /// <summary>
    /// Configure this endpoint to use inline processing (no queueing)
    /// </summary>
    public static RedisStreamEndpoint Inline(this RedisStreamEndpoint endpoint)
    {
        endpoint.Mode = EndpointMode.Inline;
        endpoint.MaxDegreeOfParallelism = 1;
        endpoint.BatchSize = 1;
        return endpoint;
    }
    
    /// <summary>
    /// Configure the consumer group to start consuming from the beginning of the stream,
    /// including any existing messages (equivalent to Redis "0-0" position)
    /// </summary>
    public static RedisStreamEndpoint StartFromBeginning(this RedisStreamEndpoint endpoint)
    {
        endpoint.StartFrom = StartFrom.Beginning;
        return endpoint;
    }
    
    /// <summary>
    /// Configure the consumer group to start consuming only new messages added after
    /// group creation (equivalent to Redis "$" position). This is the default behavior.
    /// </summary>
    public static RedisStreamEndpoint StartFromNewMessages(this RedisStreamEndpoint endpoint)
    {
        endpoint.StartFrom = StartFrom.NewMessages;
        return endpoint;
    }

    /// <summary>
    /// Helper method to create a Redis stream URI with database ID
    /// </summary>
    /// <param name="streamKey">Redis stream key name</param>
    /// <param name="databaseId">Redis database ID</param>
    /// <returns>Formatted Redis stream URI</returns>
    public static Uri BuildRedisStreamUri(string streamKey, int databaseId = 0)
    {
        return new Uri($"redis://stream/{databaseId}/{streamKey}");
    }

    /// <summary>
    /// Helper method to create a Redis stream URI with database ID and consumer group
    /// </summary>
    /// <param name="streamKey">Redis stream key name</param>
    /// <param name="databaseId">Redis database ID</param>
    /// <param name="consumerGroup">Consumer group name</param>
    /// <returns>Formatted Redis stream URI</returns>
    public static Uri BuildRedisStreamUri(string streamKey, int databaseId, string consumerGroup)
    {
        return new Uri($"redis://stream/{databaseId}/{streamKey}?consumerGroup={consumerGroup}");
    }
}
