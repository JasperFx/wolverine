namespace Wolverine.Transports;

/// <summary>
///     Marks an IChannelCallback as supporting a native dead letter queue
///     functionality
/// </summary>
public interface ISupportDeadLetterQueue
{
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

public interface IChannelCallback
{
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