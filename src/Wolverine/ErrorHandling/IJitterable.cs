namespace Wolverine.ErrorHandling;

internal interface IJitterable
{
    /// <summary>
    /// Attempts to attach a jitter strategy to this continuation.
    /// Returns true when the strategy was attached, false when the continuation
    /// has no delay to jitter (e.g. singleton instances used by RetryOnce).
    /// </summary>
    bool TrySetJitter(IJitterStrategy strategy);
}
