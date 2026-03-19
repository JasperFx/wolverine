using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.Configuration;
using Wolverine.RoutingSlip.Abstractions;
using Wolverine.RoutingSlip.Internal;
using Wolverine.RoutingSlip.Messages;
using Wolverine.RoutingSlip.Middlewares;

namespace Wolverine.RoutingSlip;

/// <summary>
/// Extension methods for Wolverine Routing Slip support.
/// </summary>
public static class WolverineRoutingSlipExtensions
{
    /// <summary>
    /// Enable Routing Slip support for Wolverine.
    /// </summary>
    public static WolverineOptions UseRoutingSlip(this WolverineOptions options,
        Action<RoutingSlipOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var routingSlipOptions = new RoutingSlipOptions();
        configure?.Invoke(routingSlipOptions);

        options.Services.TryAddSingleton<IActivityExecutor, ActivityExecutor>();
        options.Services.TryAddSingleton<IRoutingSlipCoordinator, RoutingSlipCoordinator>();

        options.Policies
            .ForMessagesOfType<RoutingSlipExecutionContext>()
            .AddMiddleware(typeof(RoutingSlipExecutionMiddleware));

        options.Policies
            .ForMessagesOfType<RoutingSlipCompensationContext>()
            .AddMiddleware(typeof(RoutingSlipCompensationMiddleware));

        options.Policies.Add(new RoutingSlipExecutionFailurePolicy(routingSlipOptions));

        return options;
    }

    /// <summary>
    /// Execute a routing slip and wait for all activities to complete.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public static ValueTask ExecuteRoutingSlip(this IMessageContext messageContext, RoutingSlip slip)
    {
        ArgumentNullException.ThrowIfNull(messageContext);
        ArgumentNullException.ThrowIfNull(slip);

        if (!slip.TryTakeNextActivity(out var first))
            throw new InvalidOperationException("No activities in the routing slip");

        var executionContext = new RoutingSlipExecutionContext(Guid.NewGuid(), first, slip);
        return messageContext.SendAsync(executionContext);
    }
}
