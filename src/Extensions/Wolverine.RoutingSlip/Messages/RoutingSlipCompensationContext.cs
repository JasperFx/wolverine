namespace Wolverine.RoutingSlip.Messages;

/// <summary>
///     A message that is sent to compensate the next activity in a routing slip
/// </summary>
/// <param name="ExecutionId"></param>
/// <param name="RoutingSlip"></param>
[Serializable]
public sealed record RoutingSlipCompensationContext(Guid ExecutionId,  RoutingSlipExecutionLog CurrentLog, RoutingSlip RoutingSlip);