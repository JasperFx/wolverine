namespace Wolverine.RoutingSlip.Messages;

/// <summary>
/// Published when a routing slip activity execution fails.
/// </summary>
/// <param name="TrackingNumber">Routing slip tracking number for correlation.</param>
/// <param name="ExceptionInfo">Serializable exception details for the failure.</param>
[Serializable]
public sealed record RoutingSlipActivityFailed(Guid TrackingNumber, ExceptionInfo ExceptionInfo);
