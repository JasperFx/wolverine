namespace Wolverine.Transports;

public static class DeadLetterQueueConstants
{
    /// <summary>
    /// Stamps the envelope headers with standard failure metadata from the given exception.
    /// </summary>
    public static void StampFailureMetadata(Envelope envelope, Exception exception)
    {
        envelope.Headers[ExceptionTypeHeader] = exception.GetType().FullName ?? "Unknown";
        envelope.Headers[ExceptionMessageHeader] = exception.Message;
        envelope.Headers[ExceptionStackHeader] = exception.StackTrace ?? "";
        envelope.Headers[FailedAtHeader] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
    }

    /// <summary>
    /// The default queue/topic name used for dead letter queues across all transports.
    /// </summary>
    public const string DefaultQueueName = "wolverine-dead-letter-queue";

    /// <summary>
    /// Header key for the full type name of the exception that caused the message to fail.
    /// </summary>
    public const string ExceptionTypeHeader = "exception-type";

    /// <summary>
    /// Header key for the exception message.
    /// </summary>
    public const string ExceptionMessageHeader = "exception-message";

    /// <summary>
    /// Header key for the exception stack trace.
    /// </summary>
    public const string ExceptionStackHeader = "exception-stack";

    /// <summary>
    /// Header key for the Unix timestamp in milliseconds when the failure occurred.
    /// </summary>
    public const string FailedAtHeader = "failed-at";
}
