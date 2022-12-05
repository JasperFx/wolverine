namespace Wolverine.Tracking;

public enum TrackingStatus
{
    /// <summary>
    ///     The session is still actively tracking outstanding
    ///     work, or no work has been detected yet
    /// </summary>
    Active,

    /// <summary>
    ///     The session determined that all activity completed
    /// </summary>
    Completed,

    /// <summary>
    ///     The session timed out without all activity completing
    /// </summary>
    TimedOut
}