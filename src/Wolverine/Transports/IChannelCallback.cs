using Wolverine.Runtime;

namespace Wolverine.Transports;

/// <summary>
///     Marks an IChannelCallback as supporting a native dead letter queue
///     functionality
/// </summary>
public interface ISupportDeadLetterQueue
{
    bool NativeDeadLetterQueueEnabled { get; }
    Task MoveToErrorsAsync(Envelope envelope, Exception exception);
}

/// <summary>
///     Marks an IChannelCallback as supporting native scheduled send
/// </summary>
public interface ISupportNativeScheduling
{
    /// <summary>
    ///     Move the current message represented by the envelope to a
    ///     scheduled delivery
    /// </summary>
    /// <param name="envelope"></param>
    /// <param name="time"></param>
    /// <returns></returns>
    Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time);
}

/// <summary>
/// Marks a listener as supporting multiple consumers reading from the same stream or queue,
/// allowing the system to differentiate between multiple listeners with the same URI
/// </summary>
public interface ISupportMultipleConsumers
{
    /// <summary>
    /// Gets a unique identifier for this specific consumer instance
    /// </summary>
    string? ConsumerId { get; internal set; }

    /// <summary>
    /// Gets the base address without any consumer-specific information
    /// </summary>
    Uri BaseAddress { get; }

    /// <summary>
    /// Gets the consumer-specific address that can be used to uniquely identify this consumer instance
    /// when storing messages
    /// </summary>
    Uri ConsumerAddress { get; }
}

public interface IChannelCallback
{
    IHandlerPipeline? Pipeline { get; }
    
    /// <summary>
    ///     Mark the message as having been successfully received and processed
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    ValueTask CompleteAsync(Envelope envelope);


    /// <summary>
    ///     Mark the incoming message as not processed
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    ValueTask DeferAsync(Envelope envelope);

    /// <summary>
    ///     Attempt to place this message back at the end of the channel queue
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    Task<bool> TryRequeueAsync(Envelope envelope)
    {
        return Task.FromResult(false);
    }
}