using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Redis.Internal;

public class RedisTransportExpression : BrokerExpression<RedisTransport, RedisStreamEndpoint, RedisStreamEndpoint, RedisListenerConfiguration, RedisSubscriberConfiguration, RedisTransportExpression>
{
    private readonly RedisTransport _transport;

    public RedisTransportExpression(RedisTransport transport, WolverineOptions options) : base(transport, options)
    {
        _transport = transport;
    }

    protected override RedisListenerConfiguration createListenerExpression(RedisStreamEndpoint listenerEndpoint)
    {
        return new RedisListenerConfiguration(listenerEndpoint);
    }

    protected override RedisSubscriberConfiguration createSubscriberExpression(RedisStreamEndpoint subscriberEndpoint)
    {
        return new RedisSubscriberConfiguration(subscriberEndpoint);
    }
    
    /// <summary>
    /// Set the Redis database ID used for the per-node reply stream endpoint (request/reply mechanics)
    /// </summary>
    public RedisTransportExpression UseReplyStreamDatabase(int databaseId)
    {
        if (databaseId < 0) throw new ArgumentOutOfRangeException(nameof(databaseId));
        _transport.ReplyDatabaseId = databaseId;
        return this;
    }

    /// <summary>
    /// Configure a default consumer name selector applied to all Redis listeners
    /// when an endpoint-level ConsumerName is not explicitly set.
    /// Example: transport.ConfigureDefaultConsumerName((rt, ep) => $"{rt.Options.ServiceName}-{rt.DurabilitySettings.AssignedNodeNumber}");
    /// </summary>
    public RedisTransportExpression ConfigureDefaultConsumerName(
        Func<IWolverineRuntime, RedisStreamEndpoint, string> selector)
    {
        _transport.DefaultConsumerNameSelector = selector;
        return this;
    }

    /// <summary>
    /// Control whether or not the system queues for intra-Wolverine communication and request/reply
    /// mechanics are enabled
    /// </summary>
    /// <param name="enabled"></param>
    /// <returns></returns>
    public RedisTransportExpression SystemQueuesEnabled(bool enabled)
    {
        _transport.SystemQueuesEnabled = enabled;
        return this;
    }
}

public class RedisListenerConfiguration : ListenerConfiguration<RedisListenerConfiguration, RedisStreamEndpoint>
{
    public RedisListenerConfiguration(RedisStreamEndpoint endpoint) : base(endpoint)
    {
    }

    internal RedisListenerConfiguration(Func<RedisStreamEndpoint> source) : base(source)
    {
    }
    
    /// <summary>
    /// Configure the batch size for reading messages from Redis streams
    /// </summary>
    public RedisListenerConfiguration BatchSize(int batchSize)
    {
        _endpoint.BatchSize = batchSize;
        return this;
    }
    
    /// <summary>
    /// Configure the consumer name for this endpoint
    /// </summary>
    public RedisListenerConfiguration ConsumerName(string consumerName)
    {
        _endpoint.ConsumerName = consumerName;
        return this;
    }
    
    /// <summary>
    /// Configure the block timeout when reading from Redis streams
    /// </summary>
    public RedisListenerConfiguration BlockTimeout(TimeSpan timeout)
    {
        _endpoint.BlockTimeoutMilliseconds = (int)timeout.TotalMilliseconds;
        return this;
    }
    
    /// <summary>
    /// Configure the consumer group to start consuming from the beginning of the stream,
    /// including any existing messages (equivalent to Redis "0-0" position)
    /// </summary>
    public RedisListenerConfiguration StartFromBeginning()
    {
        _endpoint.StartFrom = StartFrom.Beginning;
        return this;
    }
    
    /// <summary>
    /// Configure the consumer group to start consuming only new messages added after
    /// group creation (equivalent to Redis "$" position). This is the default behavior.
    /// </summary>
    public RedisListenerConfiguration StartFromNewMessages()
    {
        _endpoint.StartFrom = StartFrom.NewMessages;
        return this;
    }
    
    /// <summary>
    /// Enable auto-claiming of pending entries within the consumer loop every specified period
    /// </summary>
    /// <remarks>This can cause out of order messages.  It is recommended to manually deal with expired claimed message if you are using .Inline() or desire ordered message processing.</remarks>
    /// <param name="period">Period between auto-claim attempts (default: 30 seconds)</param>
    /// <param name="minIdle">Minimum idle time before claiming pending messages (if null, uses MinIdleBeforeClaimMilliseconds)</param>
    /// <returns>This endpoint for method chaining</returns>
    public RedisListenerConfiguration EnableAutoClaim(TimeSpan? period = null, TimeSpan? minIdle = null)
    {
        _endpoint.AutoClaimEnabled = true;
        if (period.HasValue) _endpoint.AutoClaimPeriod = period.Value;
        if (minIdle.HasValue) _endpoint.AutoClaimMinIdle = minIdle.Value;
        return this;
    }

    /// <summary>
    /// Disable auto-claiming of pending entries within the consumer loop
    /// </summary>
    /// <returns>This endpoint for method chaining</returns>
    public RedisListenerConfiguration DisableAutoClaim()
    {
        _endpoint.AutoClaimEnabled = false;
        return this;
    }
}

public class RedisSubscriberConfiguration : SubscriberConfiguration<RedisSubscriberConfiguration, RedisStreamEndpoint>
{
    internal RedisSubscriberConfiguration(RedisStreamEndpoint endpoint) : base(endpoint)
    {
    }
    
    /// <summary>
    /// Configure the batch size for reading messages from Redis streams
    /// </summary>
    public RedisSubscriberConfiguration BatchSize(int batchSize)
    {
        _endpoint.BatchSize = batchSize;
        return this;
    }
    
    /// <summary>
    /// Configure the consumer name for this endpoint
    /// </summary>
    public RedisSubscriberConfiguration ConsumerName(string consumerName)
    {
        _endpoint.ConsumerName = consumerName;
        return this;
    }
    
    /// <summary>
    /// Configure the block timeout when reading from Redis streams
    /// </summary>
    public RedisSubscriberConfiguration BlockTimeout(TimeSpan timeout)
    {
        _endpoint.BlockTimeoutMilliseconds = (int)timeout.TotalMilliseconds;
        return this;
    }
}