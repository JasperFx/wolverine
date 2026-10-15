namespace Wolverine;

/// <summary>
/// A scheduled publish and the in-process handle used to cancel its native broker schedules.
/// </summary>
public interface IScheduledMessage
{
    /// <summary>
    /// The Wolverine envelope ids created for this publish. A publish can create multiple envelopes
    /// when it is routed to multiple destinations.
    /// </summary>
    IReadOnlyList<Guid> EnvelopeIds { get; }

    /// <summary>
    /// Attempt to cancel each broker schedule created for this publish.
    /// </summary>
    /// <remarks>
    /// The handle only works while the Wolverine runtime that created it is running and does not survive
    /// an application restart. Cancellation is not queued when the broker has not accepted the scheduled
    /// send yet. A broker can also activate a scheduled message concurrently with cancellation.
    /// </remarks>
    ValueTask<IReadOnlyList<ScheduledMessageCancellationResult>> CancelAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of attempting to cancel one routed scheduled envelope.
/// </summary>
public sealed record ScheduledMessageCancellationResult(
    Guid EnvelopeId,
    ScheduledMessageCancellationStatus Status);

/// <summary>
/// Describes the outcome of a scheduled message cancellation attempt.
/// </summary>
public enum ScheduledMessageCancellationStatus
{
    /// <summary>The broker accepted the cancellation request. Activation may already be racing with it.</summary>
    CancellationAccepted,

    /// <summary>The envelope has not yet been accepted by the broker, so no cancellation was attempted.</summary>
    NotScheduledYet,

    /// <summary>The broker no longer has this sequence as a scheduled message; it may have activated or been removed.</summary>
    NoLongerScheduled,

    /// <summary>The destination does not support native scheduled message cancellation.</summary>
    NotSupported,

    /// <summary>The requested delivery time was not in the future when the message was published.</summary>
    NotScheduled
}
