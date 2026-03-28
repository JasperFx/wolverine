namespace Wolverine.RoutingSlip.Abstractions;

/// <summary>
///     An execution of a routing slip
/// </summary>
public interface IRoutingSlipExecution
{
    /// <summary>
    ///     The name of this activity
    /// </summary>
    string Name { get; }
    
    /// <summary>
    ///     The destination URI for this activity
    /// </summary>
    Uri DestinationUri { get; }
}