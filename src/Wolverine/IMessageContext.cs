﻿namespace Wolverine;

public interface IMessageContext : IMessageBus
{
    /// <summary>
    ///     Correlating identifier for the logical workflow. All envelopes sent or executed
    ///     through this context will be tracked with this identifier. If this context is the
    ///     result of a received message, this will be the original Envelope.CorrelationId
    /// </summary>
    string? CorrelationId { get; set; }

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
}