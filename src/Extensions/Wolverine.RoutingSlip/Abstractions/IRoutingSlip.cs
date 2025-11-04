namespace Wolverine.RoutingSlip.Abstractions;

/// <summary>
///     A routing slip is a collection of activities that are executed in order
/// </summary>
public interface IRoutingSlip
{
    /// <summary>
    ///     The unique tracking number for this routing slip, used to correlate events
    ///     and activities
    /// </summary>
    Guid TrackingNumber { get; }

    /// <summary>
    ///     The time when the routing slip was created
    /// </summary>
    DateTime CreateTimestamp { get; }

    /// <summary>
    ///     The list of activities that are remaining
    /// </summary>
    Queue<RoutingSlipExecution> RemainingActivities { get; }

    /// <summary>
    ///     The list of activities that have been executed
    /// </summary>
    Stack<RoutingSlipExecutionLog> ExecutedActivities { get; }
}