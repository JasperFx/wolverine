using System.Collections.Generic;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.RoutingSlip.Abstractions;
using Wolverine.RoutingSlip.Internal;
using Wolverine.RoutingSlip.Middlewares;
using Wolverine.Runtime.Handlers;
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
    /// <param name="configure">Optional hook to customize routing slip behavior</param>
    public static WolverineOptions UseRoutingSlip(this WolverineOptions options,
        Action<RoutingSlipOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        
        var routingSlipOptions = new RoutingSlipOptions();
        configure?.Invoke(routingSlipOptions);
        
        options.Services.TryAddScoped<IActivityExecutor, ActivityExecutor>();
        
        options.Policies
            .ForMessagesOfType<ExecutionContext>()
            .AddMiddleware(typeof(RoutingSlipExecutionMiddleware));
        
        options.Policies            
            .ForMessagesOfType<CompensationContext>()
            .AddMiddleware(typeof(RoutingSlipCompensationMiddleware));
        
        options.Policies.Add(new RoutingSlipExecutionFailurePolicy(routingSlipOptions));
            
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