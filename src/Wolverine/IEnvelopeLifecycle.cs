using System;
using System.Threading.Tasks;

namespace Wolverine;

public interface IEnvelopeLifecycle : IMessageBus
{
    /// <summary>
    ///     The envelope being currently handled. This will only be non-null during
    ///     the handling of a message
    /// </summary>
    Envelope? Envelope { get; }

    /// <summary>
    ///     Mark the message as having been successfully received and processed
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    ValueTask CompleteAsync();

    /// <summary>
    ///     Requeue the message for later processing
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    ValueTask DeferAsync();

    Task ReScheduleAsync(DateTimeOffset scheduledTime);

    Task MoveToDeadLetterQueueAsync(Exception exception);

    /// <summary>
    ///     Immediately execute the message again
    /// </summary>
    /// <returns></returns>
    Task RetryExecutionNowAsync();

    /// <summary>
    ///     Sends an acknowledgement back to the original sender
    /// </summary>
    /// <returns></returns>
    ValueTask SendAcknowledgementAsync();

    /// <summary>
    ///     Send a failure acknowledgement back to the original
    ///     sending service
    /// </summary>
    /// <param name="original"></param>
    /// <param name="failureDescription">Descriptive message about why the message was not successfully processed</param>
    /// <returns></returns>
    ValueTask SendFailureAcknowledgementAsync(string failureDescription);

    /// <summary>
    ///     If a messaging context is enlisted in a transaction, calling this
    ///     method will force the context to send out any outstanding messages
    ///     that were captured as part of processing the transaction
    /// </summary>
    /// <returns></returns>
    Task FlushOutgoingMessagesAsync();

    /// <summary>
    ///     Send a response message back to the original sender of the message being handled.
    ///     This can only be used from within a message handler
    /// </summary>
    /// <param name="response"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    ValueTask RespondToSenderAsync(object response);
}