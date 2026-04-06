namespace Wolverine;

/// <summary>
/// Implement this interface to create a "wire tap" that records a copy of every message
/// flowing through configured endpoints. Useful for auditing, compliance, analytics,
/// or feeding monitoring systems. Register implementations in the IoC container,
/// preferably as Singleton lifetime. Use keyed services to vary implementations
/// by endpoint.
///
/// IMPORTANT: Implementations must never allow exceptions to escape. Wolverine wraps
/// calls in try/catch as a safety net, but implementations should handle their own
/// errors internally.
/// </summary>
public interface IWireTap
{
    /// <summary>
    /// Called when a message has been successfully handled at a listening endpoint,
    /// or when a message has been successfully sent from a sending endpoint.
    /// </summary>
    ValueTask RecordSuccessAsync(Envelope envelope);

    /// <summary>
    /// Called when message handling fails at a listening endpoint after exhausting
    /// all error handling policies.
    /// </summary>
    ValueTask RecordFailureAsync(Envelope envelope, Exception exception);
}
