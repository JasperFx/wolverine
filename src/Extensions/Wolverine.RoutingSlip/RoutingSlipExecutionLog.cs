namespace Wolverine.RoutingSlip;

/// <summary>
///     Represents an executed routing slip activity entry used for compensation.
/// </summary>
/// <param name="ExecutionId"></param>
/// <param name="ExecutionName"></param>
/// <param name="DestinationUri"></param>
public sealed record RoutingSlipExecutionLog(Guid ExecutionId, string ExecutionName, Uri DestinationUri);
