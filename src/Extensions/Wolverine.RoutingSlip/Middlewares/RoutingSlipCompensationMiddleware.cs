using Wolverine.RoutingSlip.Abstractions;
using Wolverine.RoutingSlip.Messages;

namespace Wolverine.RoutingSlip.Middlewares;

/// <summary>
/// Middleware that delegates successful compensation transitions to the coordinator.
/// </summary>
public sealed class RoutingSlipCompensationMiddleware
{
    public ValueTask AfterAsync(RoutingSlipCompensationContext msg, IMessageContext context,
        IRoutingSlipCoordinator coordinator)
    {
        return coordinator.OnCompensationSucceededAsync(context, msg);
    }
}
