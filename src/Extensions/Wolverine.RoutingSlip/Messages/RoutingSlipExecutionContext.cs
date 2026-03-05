namespace Wolverine.RoutingSlip.Messages;

/// <summary>
///     A message that is sent to execute the next activity in a routing slip
/// </summary>
/// <param name="Id"></param>
/// <param name="RoutingSlip"></param>
[Serializable]
public sealed record RoutingSlipExecutionContext(Guid Id,  RoutingSlipExecution? CurrentActivity, RoutingSlip RoutingSlip);