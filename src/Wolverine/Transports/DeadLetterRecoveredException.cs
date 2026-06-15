using Wolverine.Persistence.Durability;

namespace Wolverine.Transports;

/// <summary>
/// Synthetic exception used when a natively dead-lettered broker message is recovered into
/// Wolverine's durable dead-letter storage. It carries the original exception type name and
/// message reconstructed from the transport's native dead-letter metadata (RabbitMQ <c>x-death</c>,
/// SQS failure headers, Azure Service Bus <c>DeadLetterReason</c>/<c>DeadLetterErrorDescription</c>),
/// and implements <see cref="IDeadLetterExceptionInfo"/> so the durable store records the original
/// exception type rather than this wrapper. That keeps dead letters triageable by type in tools
/// like CritterWatch.
/// </summary>
public class DeadLetterRecoveredException : Exception, IDeadLetterExceptionInfo
{
    public DeadLetterRecoveredException(string? originalExceptionType, string message) : base(message)
    {
        ExceptionType = string.IsNullOrWhiteSpace(originalExceptionType) ? "Unknown" : originalExceptionType!;
    }

    /// <summary>
    /// The original exception type name, as reconstructed from the transport's native dead-letter
    /// metadata. Persisted to durable storage in place of this wrapper's runtime type.
    /// </summary>
    public string ExceptionType { get; }

    public string ExceptionMessage => Message;

    public override string ToString() => $"{ExceptionType}: {Message}";
}
