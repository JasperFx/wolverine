using Wolverine.RoutingSlip.Abstractions;

namespace Wolverine.RoutingSlip;

/// <inheritdoc />
public sealed record RoutingSlip(Guid TrackingNumber, DateTime CreateTimestamp, 
    Queue<RoutingSlipExecution> RemainingActivities, 
    Stack<RoutingSlipExecutionLog> ExecutedActivities)
    : IRoutingSlip;