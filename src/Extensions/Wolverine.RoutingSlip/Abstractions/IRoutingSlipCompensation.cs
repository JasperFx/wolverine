namespace Wolverine.RoutingSlip.Abstractions;

/// <summary>
///     A compensation for a routing slip
/// </summary>
public interface IRoutingSlipCompensation
{
    /// <summary>
    /// The unique execution Id for this routing slip compensation
    /// </summary>
    Guid ExecutionId { get; }
    
    /// <summary>
    /// The destination URI for this compensation
    /// </summary>
    Uri DestinationUri { get; }
}