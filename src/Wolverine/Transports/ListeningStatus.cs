namespace Wolverine.Transports;

public enum ListeningStatus
{
    Accepting,
    TooBusy,
    Stopped,
    Unknown,
    GloballyLatched,

    /// <summary>
    /// The listener was deliberately paused — an operator command, a circuit breaker trip, or any
    /// other IListenerCircuit.PauseAsync() caller — and will NOT self-resume on back-pressure
    /// relief. Distinct from <see cref="TooBusy"/> (back-pressure latched, self-recovering) so
    /// state readers can tell operator intent from transient load (GH-3832). Appended last so the
    /// existing members keep their numeric values on the wire.
    /// </summary>
    Paused
}