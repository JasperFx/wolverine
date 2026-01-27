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
    public static RedisTransportExpression UseRedisTransport(this WolverineOptions options, string connectionString)
    {
        var transport = new RedisTransport(connectionString);
        
        options.Transports.Add(transport);
        
        return new RedisTransportExpression(transport, options);
    }

    /// <summary>
    /// Configure Wolverine to publish messages to the specified Redis stream (uses database 0)
    /// </summary>
    /// <param name="publishing">Publishing configuration</param>
    /// <param name="streamKey">Redis stream key name</param>
    /// <returns>Stream endpoint for further configuration</returns>
    public static RedisSubscriberConfiguration ToRedisStream(this IPublishToExpression publishing, string streamKey)
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
    public static RedisSubscriberConfiguration ToRedisStream(this IPublishToExpression publishing, string streamKey, int databaseId)
    {
        // Use correct pattern from Kafka transport
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<RedisTransport>();
        
        var endpoint = transport.StreamEndpoint(streamKey, databaseId);
        
        // Register this as a publishing destination
        publishing.To(endpoint.Uri);
        
        return new RedisSubscriberConfiguration(endpoint);
    }

    /// <summary>
    /// Configure Wolverine to listen to messages from the specified Redis stream with a consumer group (uses database 0)
    /// </summary>
    /// <param name="options">Wolverine configuration options</param>
    /// <param name="streamKey">Redis stream key name</param>
    /// <param name="consumerGroup">Consumer group name</param>
    /// <returns>Stream endpoint for further configuration</returns>
    public static RedisListenerConfiguration ListenToRedisStream(this WolverineOptions options, string streamKey, string consumerGroup)
    {
        return options.ListenToRedisStream(streamKey, consumerGroup, 0);
    }

    /// <summary>
    /// Configure Wolverine to listen to a Redis stream with starting position control
    /// </summary>
    public static RedisListenerConfiguration ListenToRedisStream(this WolverineOptions options, string streamKey, string consumerGroup, StartFrom startFrom)
    {
        var endpoint = options.ListenToRedisStream(streamKey, consumerGroup, 0);

        if (startFrom == StartFrom.Beginning) return endpoint.StartFromBeginning();
        return endpoint.StartFromNewMessages();
    }

    /// <summary>
    /// Configure Wolverine to listen to messages from the specified Redis stream with a consumer group and database ID
    /// </summary>
    /// <param name="options">Wolverine configuration options</param>
    /// <param name="streamKey">Redis stream key name</param>
    /// <param name="consumerGroup">Consumer group name</param>
    /// <param name="databaseId">Redis database ID</param>
    /// <returns>Stream endpoint for further configuration</returns>
    public static RedisListenerConfiguration ListenToRedisStream(this WolverineOptions options, string streamKey, string consumerGroup, int databaseId)
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

        return new RedisListenerConfiguration(endpoint);
    }


}
