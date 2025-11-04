namespace Wolverine.RoutingSlip;

/// <summary>
///     A message that is sent to indicate that the routing slip has finished
/// </summary>
/// <param name="ExecutionId"></param>
/// <param name="DestinationUri"></param>
public sealed record RoutingSlipExecutionLog(Guid ExecutionId, string ExecutionName, Uri DestinationUri);