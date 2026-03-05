using Wolverine.RoutingSlip.Messages;

namespace Wolverine.RoutingSlip.Abstractions;

/// <summary>
/// Coordinates routing slip execution and compensation transitions.
/// </summary>
public interface IRoutingSlipCoordinator
{
    ValueTask OnExecutionSucceededAsync(IMessageContext context, RoutingSlipExecutionContext message);

    ValueTask OnExecutionFailedAsync(IEnvelopeLifecycle context, RoutingSlipExecutionContext message, Exception exception);

    ValueTask OnCompensationSucceededAsync(IMessageContext context, RoutingSlipCompensationContext message);

    ValueTask OnCompensationFailedAsync(IEnvelopeLifecycle context, RoutingSlipCompensationContext message, Exception exception);
}
