namespace Wolverine.RabbitMQ;

public interface IRabbitMqQueue
{
    string QueueName { get; }

    /// <summary>
    ///     If true, this queue will be deleted when the connection is closed. This is mostly useful
    ///     for temporary, response queues
    /// </summary>
    bool AutoDelete { get; set; }

    /// <summary>
    ///     If true, this queue can only be used by a single connection
    /// </summary>
    bool IsExclusive { get; set; }

    /// <summary>
    ///     The default is true. Governs whether queue messages
    /// </summary>
    bool IsDurable { get; set; }

    /// <summary>
    ///     Arguments for Rabbit MQ queue declarations. See the Rabbit MQ .NET client documentation at
    ///     https://www.rabbitmq.com/dotnet.html
    /// </summary>
    IDictionary<string, object> Arguments { get; }

    /// <summary>
    ///     Declare that Wolverine should purge the existing queue
    ///     of all existing messages on startup
    /// </summary>
    bool PurgeOnStartup { get; set; }

    /// <summary>
    ///     Create a "time to live" limit for messages in this queue. Sets the Rabbit MQ x-message-ttl argument on a queue
    /// </summary>
    /// <param name="limit"></param>
    void TimeToLive(TimeSpan limit);
}