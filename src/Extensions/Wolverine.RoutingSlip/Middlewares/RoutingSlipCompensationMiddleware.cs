using Microsoft.Extensions.Logging;
using Wolverine.RoutingSlip.Abstractions;

namespace Wolverine.RoutingSlip.Middlewares;

/// <summary>
///     A middleware that compensates the next activity in a routing slip
/// </summary>
public sealed class RoutingSlipCompensationMiddleware
{
    /// <summary>
    ///     After processing a message, if there are more activities to compensate, compensate the next activity
    /// </summary>
    /// <param name="msg"></param>
    /// <param name="context"></param>
    /// <param name="executor"></param>
    /// <param name="logger"></param>
    public async ValueTask AfterAsync(CompensationContext msg, IMessageContext context, 
        IActivityExecutor executor, ILogger logger)
    {
        if (msg.RoutingSlip.TryGetExecutedActivity(out var activity))
        {
            await executor.CompensateAsync(context, msg.RoutingSlip, activity);
        }
        else
        {
            logger.LogDebug("No more compensations for {TrackingNumber}", msg.RoutingSlip.TrackingNumber);
        }
    }
}