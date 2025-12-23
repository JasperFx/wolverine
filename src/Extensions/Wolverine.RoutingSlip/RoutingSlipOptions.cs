using Wolverine.ErrorHandling;

namespace Wolverine.RoutingSlip;

/// <summary>
///     Optional customizations for routing slips
/// </summary>
public sealed class RoutingSlipOptions
{
    public Action<PolicyExpression>? OverridePolicy { get; set; }
}