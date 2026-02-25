namespace Wolverine.RoutingSlip.Abstractions;

/// <summary>
/// A routing slip is a collection of activities that are executed in order.
/// </summary>
public interface IRoutingSlip
{
    Guid TrackingNumber { get; }

    DateTime CreateTimestamp { get; }

    IReadOnlyList<RoutingSlipExecution> RemainingActivities { get; }

    IReadOnlyList<RoutingSlipExecutionLog> ExecutedActivities { get; }
}
