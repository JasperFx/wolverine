using Wolverine.RoutingSlip.Messages;

namespace Wolverine.RoutingSlip.Abstractions;

public interface IRoutingSlipCompensationActivity
{
    ValueTask HandleAsync(RoutingSlipCompensationContext context, CancellationToken ct);
}