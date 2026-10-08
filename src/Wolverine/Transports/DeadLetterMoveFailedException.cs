namespace Wolverine.Transports;

/// <summary>
///     Thrown by <see cref="ISupportDeadLetterQueue.MoveToErrorsAsync" /> when the transport could not move the
///     envelope to its dead letter queue and has left the original delivery with the broker, which will deliver it
///     again. Nothing was dead lettered and nothing was settled, so whoever asked for the move must not complete the
///     envelope: that would settle, and so lose, the only copy.
/// </summary>
public class DeadLetterMoveFailedException : Exception
{
    public DeadLetterMoveFailedException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
