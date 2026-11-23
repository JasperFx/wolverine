namespace Wolverine.Transports;

/// <summary>
/// GH-5926. A listener that back pressure can pause in place instead of stopping. The back pressure agent normally
/// stops and disposes a listener whose endpoint holds too many messages, and builds a new one once the backlog
/// drains. For a listener that only settles a delivery once its message finishes (see
/// <see cref="ISettlesAfterHandlingListener"/>), that hands the unfinished backlog back to the broker while this node
/// is still working through it, and the broker delivers it again. A listener that implements this stops taking new
/// deliveries while paused but keeps its broker connection, and keeps settling the deliveries it already handed out.
/// <para>
/// Only the back pressure latch uses this. Every other stop (shutdown, a circuit breaker pause, an agent restriction,
/// a forced restart) stops and disposes the listener as before, paused or not.
/// </para>
/// </summary>
internal interface IPausesForBackPressure : IListener
{
    /// <summary>
    /// Whether this listener, as configured, pauses in place. False means stop and dispose it as usual
    /// </summary>
    bool PausesForBackPressure { get; }

    /// <summary>
    /// Stop taking new deliveries, keeping the broker connection and settling the deliveries already handed out
    /// </summary>
    ValueTask PauseForBackPressureAsync();

    /// <summary>
    /// Take new deliveries again after <see cref="PauseForBackPressureAsync"/>
    /// </summary>
    ValueTask ResumeAfterBackPressureAsync();
}
