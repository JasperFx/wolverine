using System.Globalization;

namespace Wolverine.Transports;

public static class DeadLetterQueueConstants
{
    /// <summary>
    /// Stamps the envelope headers with standard failure metadata from the given exception:
    /// exception type, message, truncated stack trace, the time of the failure, and the
    /// original destination (plus partition/offset for partitioned brokers like Kafka)
    /// the message was received from. See GH-3474.
    /// </summary>
    public static void StampFailureMetadata(Envelope envelope, Exception exception)
    {
        envelope.Headers[ExceptionTypeHeader] = exception.GetType().FullName ?? "Unknown";
        envelope.Headers[ExceptionMessageHeader] = exception.Message;
        envelope.Headers[ExceptionStackHeader] = TruncateStackTrace(exception.StackTrace);
        envelope.Headers[FailedAtHeader] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);

        if (envelope.Destination != null)
        {
            envelope.Headers[OriginalDestinationHeader] = envelope.Destination.ToString();
        }

        if (envelope.PartitionId.HasValue)
        {
            envelope.Headers[OriginalPartitionHeader] =
                envelope.PartitionId.Value.ToString(CultureInfo.InvariantCulture);
            envelope.Headers[OriginalOffsetHeader] = envelope.Offset.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Caps the stamped stack trace so the header stays within broker header/attribute
    /// size limits
    /// </summary>
    public static string TruncateStackTrace(string? stackTrace)
    {
        if (stackTrace == null) return string.Empty;
        if (stackTrace.Length <= MaxStackTraceLength) return stackTrace;

        return stackTrace[..MaxStackTraceLength] + TruncationMarker;
    }

    /// <summary>
    /// Maximum number of characters of the exception stack trace that will be stamped
    /// into the exception-stack header
    /// </summary>
    public const int MaxStackTraceLength = 8192;

    /// <summary>
    /// Appended to the exception-stack header value when the stack trace was cut off
    /// at MaxStackTraceLength characters
    /// </summary>
    public const string TruncationMarker = "... (truncated)";

    /// <summary>
    /// All header keys stamped by StampFailureMetadata, for transports that need to copy
    /// the diagnostic headers onto a broker-native dead letter operation (e.g. Azure
    /// Service Bus propertiesToModify)
    /// </summary>
    public static readonly string[] DiagnosticHeaders =
    [
        ExceptionTypeHeader,
        ExceptionMessageHeader,
        ExceptionStackHeader,
        FailedAtHeader,
        OriginalDestinationHeader,
        OriginalPartitionHeader,
        OriginalOffsetHeader
    ];

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
    /// Header key for the exception stack trace, truncated to MaxStackTraceLength characters.
    /// </summary>
    public const string ExceptionStackHeader = "exception-stack";

    /// <summary>
    /// Header key for the Unix timestamp in milliseconds when the failure occurred.
    /// </summary>
    public const string FailedAtHeader = "failed-at";

    /// <summary>
    /// Header key for the Wolverine endpoint Uri the message was originally received from
    /// before it was moved to a dead letter or retry destination.
    /// </summary>
    public const string OriginalDestinationHeader = "original-destination";

    /// <summary>
    /// Header key for the broker partition the message was originally received from, when
    /// the transport is partitioned (e.g. Kafka).
    /// </summary>
    public const string OriginalPartitionHeader = "original-partition";

    /// <summary>
    /// Header key for the broker offset of the originally received message, when the
    /// transport tracks offsets (e.g. Kafka).
    /// </summary>
    public const string OriginalOffsetHeader = "original-offset";
}
