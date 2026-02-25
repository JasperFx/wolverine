using Wolverine.RoutingSlip.Messages;

namespace Wolverine.RoutingSlip.Abstractions;

public interface IRoutingSlipExecutionActivity
{
    ValueTask HandleAsync(RoutingSlipExecutionContext context, CancellationToken ct);
}