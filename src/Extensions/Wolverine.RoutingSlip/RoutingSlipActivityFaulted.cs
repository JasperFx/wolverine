namespace Wolverine.RoutingSlip;

/// <summary>
/// Published when a routing slip activity execution fails.
/// </summary>
/// <param name="TrackingNumber">Routing slip tracking number for correlation.</param>
/// <param name="ExceptionInfo">Serializable exception details for the failure.</param>
public sealed record RoutingSlipActivityFaulted(Guid TrackingNumber, ExceptionInfo ExceptionInfo);
