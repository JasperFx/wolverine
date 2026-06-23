namespace Wolverine.Transports;

/// <summary>
/// The health of a listener's background receive/poll loop. Surfaced on
/// <see cref="Wolverine.Configuration.EndpointHealthSnapshot"/> so external monitors can detect a listener whose
/// receive loop has faulted or hung while its orchestration <c>ListeningStatus</c> still reports <c>Accepting</c> —
/// a failure mode that connection state alone (GH-3231) cannot see.
/// </summary>
public enum ReceiveLoopStatus
{
    /// <summary>
    /// The listener has no managed receive loop, or does not report loop health. This is the default.
    /// </summary>
    Unknown,

    /// <summary>
    /// The receive loop has been created but has not started iterating yet.
    /// </summary>
    NotStarted,

    /// <summary>
    /// The receive loop is running and iterating.
    /// </summary>
    Running,

    /// <summary>
    /// The receive loop has stopped cleanly (e.g. the listener was paused/stopped).
    /// </summary>
    Stopped,

    /// <summary>
    /// The receive loop terminated on an unexpected exception and is no longer consuming. The listener is silently
    /// dead even though its <c>ListeningStatus</c> may still read <c>Accepting</c>.
    /// </summary>
    Faulted
}

/// <summary>
/// Optional capability implemented by an <see cref="IListener"/> (or the loop it owns) that runs a long-lived
/// background receive/poll loop and can report that loop's liveness. Listeners with no such loop simply do not
/// implement this and report <see cref="ReceiveLoopStatus.Unknown"/>.
/// </summary>
public interface IReportReceiveLoopHealth
{
    /// <summary>
    /// The current status of the background receive loop.
    /// </summary>
    ReceiveLoopStatus ReceiveLoopStatus { get; }

    /// <summary>
    /// Approximate timestamp of the last receive-loop iteration (a heartbeat the loop bumps each pass). A value that
    /// stops advancing while the listener reports <c>Accepting</c> indicates a hung loop. Null when never started.
    /// </summary>
    DateTimeOffset? LastReceiveLoopActivityAt { get; }
}
