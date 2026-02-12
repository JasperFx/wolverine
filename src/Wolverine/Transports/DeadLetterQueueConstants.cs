namespace Wolverine.Transports;

public static class DeadLetterQueueConstants
{
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
