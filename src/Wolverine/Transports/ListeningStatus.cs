namespace Wolverine.Transports;

public enum ListeningStatus
{
    Accepting,
    TooBusy,
    Stopped,
    Unknown,
    GloballyLatched,

    /// <summary>
    /// The listener was deliberately paused for a fixed interval — a circuit breaker trip, a
    /// PauseListener error policy, or any other <c>PauseAsync(TimeSpan)</c> caller. It resumes on
    /// its own when that interval elapses; every caller installs a restart timer, so this state is
    /// temporary by construction and is NOT a report that a listener needs intervention.
    ///
    /// What it does mean is that recovery is on a clock rather than on load. Distinct from
    /// <see cref="TooBusy"/>, which is back-pressure latched and resumes only once the queue
    /// drains below the restart threshold — so Wolverine's back-pressure agent never restarts a
    /// paused listener, and a reader watching a queue grow can tell "waiting for the pause to
    /// elapse" from "waiting for the backlog to clear" (GH-3832).
    ///
    /// Appended last so the existing members keep their numeric values on the wire.
    /// </summary>
    Paused
}