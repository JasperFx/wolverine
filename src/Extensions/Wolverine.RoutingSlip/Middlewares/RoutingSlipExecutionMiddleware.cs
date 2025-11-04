using Microsoft.Extensions.Logging;
using Wolverine.RoutingSlip.Abstractions;

namespace Wolverine.RoutingSlip.Middlewares;

/// <summary>
///     A middleware that executes the next activity in a routing slip
/// </summary>
public sealed class RoutingSlipExecutionMiddleware
{
    /// <summary>
    ///     After processing a message, if there are more activities to execute, execute the next activity
    /// </summary>
    /// <param name="msg"></param>
    /// <param name="context"></param>
    /// <param name="envelope"></param>
    /// <param name="executor"></param>
    /// <param name="logger"></param>
    public async ValueTask AfterAsync(ExecutionContext msg, IMessageContext context, 
        IActivityExecutor executor, ILogger logger)
    {
        if (msg.CurrentActivity is not null)
        {
            msg.RoutingSlip.AddExecutedActivity(new RoutingSlipExecutionLog(msg.Id, msg.CurrentActivity.Name,
                msg.CurrentActivity.DestinationUri));
        }

        if (msg.RoutingSlip.TryGetRemainingActivity(out var activity))
        {
            await executor.ExecuteAsync(context, msg.RoutingSlip, activity);
        }
        else
        {
            logger.LogDebug("No more activities for {TrackingNumber}", msg.RoutingSlip.TrackingNumber);
        }
    }
}
