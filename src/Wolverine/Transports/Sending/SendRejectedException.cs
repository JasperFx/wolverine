namespace Wolverine.Transports.Sending;

/// <summary>
///     GH-4955. The destination refused the message, and retrying cannot change its answer: a JetStream
///     stream that is full under DiscardNew, a failed Nats-Expected-* check, a message over the broker's size
///     limit. A transport throws this from its sender -- with the broker's own exception as the inner one --
///     to say so. An inline sending agent then makes no retry and throws it to the caller of SendAsync();
///     durable and buffered endpoints keep the message in the outbox and apply their existing failure handling.
///     A connectivity failure is not a rejection and keeps the ordinary retry.
/// </summary>
public class SendRejectedException : Exception
{
    public SendRejectedException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
