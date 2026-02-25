using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.RoutingSlip.Abstractions;
using Wolverine.RoutingSlip.Messages;
using Wolverine.Runtime.Handlers;

namespace Wolverine.RoutingSlip.Internal;

/// <summary>
/// Applies either the user-provided routing slip failure policy or the default discard behavior.
/// The default failure transition orchestration is delegated to <see cref="IRoutingSlipCoordinator" />.
/// </summary>
/// <param name="routingSlipOptions"></param>
internal sealed class RoutingSlipExecutionFailurePolicy(RoutingSlipOptions routingSlipOptions) : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, JasperFx.IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            if (chain.MessageType != typeof(RoutingSlipExecutionContext) &&
                chain.MessageType != typeof(RoutingSlipCompensationContext))
            {
                continue;
            }

            var expression = chain.OnAnyException();

            if (routingSlipOptions.OverridePolicy is not null)
            {
                routingSlipOptions.OverridePolicy(expression);
                continue;
            }

            expression
                .Discard()
                .And(async (runtime, context, exception) =>
                {
                    var message = context.Envelope?.Message;
                    var coordinator = runtime.Services.GetRequiredService<IRoutingSlipCoordinator>();

                    switch (message)
                    {
                        case RoutingSlipExecutionContext execution:
                            await coordinator.OnExecutionFailedAsync(context, execution, exception);
                            break;

                        case RoutingSlipCompensationContext compensation:
                            await coordinator.OnCompensationFailedAsync(context, compensation, exception);
                            break;
                    }
                });
        }
    }
}
