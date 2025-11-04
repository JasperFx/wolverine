using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.ErrorHandling;
using Wolverine.RoutingSlip.Abstractions;
using Wolverine.RoutingSlip.Internal;
using Wolverine.RoutingSlip.Middlewares;
using Wolverine.Tracking;

namespace Wolverine.RoutingSlip;

/// <summary>
///     Extension methods for Wolverine Routing Slip support
/// </summary>
public static class WolverineRoutingSlipExtensions
{
    /// <summary>
    ///     Enable Routing Slip support for Wolverine
    /// </summary>
    /// <param name="options"></param>
    public static WolverineOptions UseRoutingSlip(this WolverineOptions options, 
        Action<PolicyExpression>? configureSlipErrors = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        
        options.Services.TryAddScoped<IActivityExecutor, ActivityExecutor>();
        
        options.Policies
            .ForMessagesOfType<ExecutionContext>()
            .AddMiddleware(typeof(RoutingSlipExecutionMiddleware));
        
        options.Policies            
            .ForMessagesOfType<CompensationContext>()
            .AddMiddleware(typeof(RoutingSlipCompensationMiddleware));
        
        options.Policies
            .OnAnyException()
            .Discard()
            .And(async (_, context, _) =>
            {
                if (context.Envelope?.Message is ExecutionContext exec && 
                    exec.RoutingSlip.ExecutedActivities.TryPop(out var next))
                {
                    await context.SendAsync(
                        new CompensationContext(
                            exec.Id,
                            next,
                            exec.RoutingSlip));
                }
            });
            
        return options;
    }

    /// <summary>
    ///     Execute a routing slip and wait for all activities to complete
    /// </summary>
    /// <param name="messageContext"></param>
    /// <param name="slip"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static ValueTask ExecuteRoutingSlip(this IMessageContext messageContext, RoutingSlip slip)
    {
        ArgumentNullException.ThrowIfNull(messageContext);
        ArgumentNullException.ThrowIfNull(slip);

        if (!slip.TryGetRemainingActivity(out var first))
            throw new InvalidOperationException("No activities in the routing slip");

        var executionContext = new ExecutionContext(Guid.NewGuid(), first, slip);
        return messageContext.SendAsync(executionContext);
    }
}