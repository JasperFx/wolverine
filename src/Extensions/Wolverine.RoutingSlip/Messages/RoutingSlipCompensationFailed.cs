namespace Wolverine.RoutingSlip.Messages;

/// <summary>
/// Published when compensation processing fails for a routing slip.
/// </summary>
/// <param name="TrackingNumber">Routing slip tracking number for correlation.</param>
/// <param name="ExceptionInfo">Serializable exception details for the compensation failure.</param>
[Serializable]
public sealed record RoutingSlipCompensationFailed(Guid TrackingNumber, ExceptionInfo ExceptionInfo);
