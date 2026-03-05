namespace Wolverine;

public interface IMessageContext : IMessageBus
{
    /// <summary>
    ///     Correlating identifier for the logical workflow. All envelopes sent or executed
    ///     through this context will be tracked with this identifier. If this context is the
    ///     result of a received message, this will be the original Envelope.CorrelationId
    /// </summary>
    string? CorrelationId { get; set; }

    /// <summary>
    ///     The authenticated user name for tracking and auditing purposes.
    ///     When EnableRelayOfUserName is true, this is automatically propagated
    ///     to outgoing messages and Marten's LastModifiedBy.
    /// </summary>
    string? UserName { get; set; }

    /// <summary>
    ///     The envelope being currently handled. This will only be non-null during
    ///     the handling of a message
    /// </summary>
    Envelope? Envelope { get; }

    /// <summary>
    ///     Send a response message back to the original sender of the message being handled.
    ///     This can only be used from within a message handler
    /// </summary>
    /// <param name="response"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    ValueTask RespondToSenderAsync(object response);

    /// <summary>
    ///     Reschedule the current message for redelivery at a later time
    ///     This can only be used from within a message handler
    /// </summary>
    /// <param name="rescheduledAt"></param>
    /// <param name="response"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    Task ReScheduleCurrentAsync(DateTimeOffset rescheduledAt);
}