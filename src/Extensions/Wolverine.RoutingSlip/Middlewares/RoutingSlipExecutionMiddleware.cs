using Wolverine.RoutingSlip.Abstractions;
using Wolverine.RoutingSlip.Messages;

namespace Wolverine.RoutingSlip.Middlewares;

/// <summary>
/// Middleware that delegates successful execution transitions to the coordinator.
/// </summary>
public sealed class RoutingSlipExecutionMiddleware
{
    public ValueTask AfterAsync(RoutingSlipExecutionContext msg, IMessageContext context,
        IRoutingSlipCoordinator coordinator)
    {
        return coordinator.OnExecutionSucceededAsync(context, msg);
    }
}
