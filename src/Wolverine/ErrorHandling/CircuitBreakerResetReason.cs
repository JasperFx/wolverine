namespace Wolverine.ErrorHandling;

/// <summary>
/// How a listener paused by a tripped circuit breaker came to accept messages again, as reported to
/// <c>IWolverineObserver.CircuitBreakerReset(Endpoint, CircuitBreakerResetReason)</c>. See GH-5446.
/// </summary>
public enum CircuitBreakerResetReason
{
    /// <summary>
    /// The breaker's own restart fired once <see cref="CircuitBreakerOptions.PauseTime" /> elapsed.
    /// </summary>
    PauseTimeElapsed,

    /// <summary>
    /// Something started or restarted the listener before <see cref="CircuitBreakerOptions.PauseTime" /> was
    /// over -- an operator through the control endpoint, a call to <c>IListenerCircuit.StartAsync()</c> or
    /// <c>RestartAsync()</c>.
    /// </summary>
    ExplicitStart
}
