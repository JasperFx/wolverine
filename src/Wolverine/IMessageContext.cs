using System;
using System.Threading.Tasks;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Persistence.Durability;

namespace Wolverine;

public interface IMessageContext : IMessagePublisher
{
    /// <summary>
    ///     Correlating identifier for the logical workflow. All envelopes sent or executed
    ///     through this context will be tracked with this identifier. If this context is the
    ///     result of a received message, this will be the original Envelope.CorrelationId
    /// </summary>
    string? CorrelationId { get; }

    /// <summary>
    ///     The envelope being currently handled. This will only be non-null during
    ///     the handling of a message
    /// </summary>
    Envelope? Envelope { get; }

    /// <summary>
    ///     The active envelope persistence for the application. This is used
    ///     by the "outbox" support in Wolverine
    /// </summary>
    IEnvelopePersistence? Persistence { get; }

    /// <summary>
    /// Current envelope outbox
    /// </summary>
    IEnvelopeOutbox? Outbox { get; }

    /// <summary>
    /// If a messaging context is enlisted in a transaction, calling this
    /// method will force the context to send out any outstanding messages
    /// that were captured as part of processing the transaction
    /// </summary>
    /// <returns></returns>
    Task FlushOutgoingMessagesAsync();

    /// <summary>
    /// Enlist this context within some kind of existing business
    /// transaction so that messages are only sent if the transaction succeeds.
    /// Wolverine's "Outbox" support
    /// </summary>
    /// <param name="outbox"></param>
    /// <returns></returns>
    Task EnlistInOutboxAsync(IEnvelopeOutbox outbox);

    /// <summary>
    /// Start a new transaction. This is not valid if already enlisted in an envelope transaction
    /// </summary>
    /// <param name="outbox"></param>
    void EnlistInOutbox(IEnvelopeOutbox outbox);

    /// <summary>
    ///     Opt into using an in memory transaction for the execution context.
    /// </summary>
    ValueTask UseInMemoryTransactionAsync();

    /// <summary>
    ///     Send a response message back to the original sender of the message being handled.
    ///     This can only be used from within a message handler
    /// </summary>
    /// <param name="response"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    ValueTask RespondToSenderAsync(object response);

    /// <summary>
    ///     Enqueue a cascading message to the outstanding context transaction
    ///     Can be either the message itself, any kind of ISendMyself object,
    ///     or an IEnumerable<object>
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    Task EnqueueCascadingAsync(object message);


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
}
