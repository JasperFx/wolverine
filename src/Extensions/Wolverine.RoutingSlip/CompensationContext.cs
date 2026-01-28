namespace Wolverine.RoutingSlip;

/// <summary>
///     A message that is sent to compensate the next activity in a routing slip
/// </summary>
/// <param name="ExecutionId"></param>
/// <param name="RoutingSlip"></param>
public sealed record CompensationContext(Guid ExecutionId,  RoutingSlipExecutionLog CurrentLog, RoutingSlip RoutingSlip);