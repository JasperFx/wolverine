namespace Wolverine.RoutingSlip;

/// <summary>
///     A message that is sent to execute the next activity in a routing slip
/// </summary>
/// <param name="Id"></param>
/// <param name="RoutingSlip"></param>
public sealed record ExecutionContext(Guid Id,  RoutingSlipExecution? CurrentActivity, RoutingSlip RoutingSlip);