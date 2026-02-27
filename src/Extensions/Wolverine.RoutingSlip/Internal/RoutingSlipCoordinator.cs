using Microsoft.Extensions.Logging;
using Wolverine.RoutingSlip.Abstractions;
using Wolverine.RoutingSlip.Messages;

namespace Wolverine.RoutingSlip.Internal;

internal sealed class RoutingSlipCoordinator(
    IActivityExecutor executor,
    ILogger<RoutingSlipCoordinator> logger) : IRoutingSlipCoordinator
{
    public async ValueTask OnExecutionSucceededAsync(IMessageContext context, RoutingSlipExecutionContext message)
    {
        if (message.CurrentActivity is { } current)
        {
            message.RoutingSlip.MarkActivityExecuted(message.Id, current);
        }

        if (message.RoutingSlip.TryTakeNextActivity(out var next))
        {
            await executor.ExecuteAsync(context, message.RoutingSlip, next);
            return;
        }

        logger.LogDebug("No more activities for {TrackingNumber}", message.RoutingSlip.TrackingNumber);
    }

    public async ValueTask OnExecutionFailedAsync(IEnvelopeLifecycle context, RoutingSlipExecutionContext message, Exception exception)
    {
        await context.PublishAsync(new RoutingSlipActivityFailed(
            message.RoutingSlip.TrackingNumber,
            ExceptionInfo.From(exception)));

        if (message.RoutingSlip.TryTakeLastExecutedActivity(out var compensation))
        {
            await context.EndpointFor(compensation.DestinationUri).SendAsync(
                new RoutingSlipCompensationContext(compensation.ExecutionId, compensation, message.RoutingSlip));
            return;
        }

        logger.LogDebug("No compensations required for {TrackingNumber}", message.RoutingSlip.TrackingNumber);
    }

    public async ValueTask OnCompensationSucceededAsync(IMessageContext context, RoutingSlipCompensationContext message)
    {
        if (message.RoutingSlip.TryTakeLastExecutedActivity(out var next))
        {
            await executor.CompensateAsync(context, message.RoutingSlip, next);
            return;
        }

        logger.LogDebug("No more compensations for {TrackingNumber}", message.RoutingSlip.TrackingNumber);
    }

    public ValueTask OnCompensationFailedAsync(IEnvelopeLifecycle context, RoutingSlipCompensationContext message, Exception exception)
    {
        return context.PublishAsync(new RoutingSlipCompensationFailed(
            message.RoutingSlip.TrackingNumber,
            ExceptionInfo.From(exception)));
    }
}
