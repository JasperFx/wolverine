using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Wolverine.RoutingSlip.Internal;

/// <summary>
///     Applies either the user-provided routing slip failure policy or the default discard + compensation behavior
///     to handler chains that process <see cref="ExecutionContext" /> messages.
/// </summary>
/// <param name="routingSlipOptions"></param>
internal sealed class RoutingSlipExecutionFailurePolicy(RoutingSlipOptions routingSlipOptions) : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, JasperFx.IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            if (chain.MessageType != typeof(ExecutionContext))
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
                .And(async (_, context, _) =>
                {
                    if (context.Envelope?.Message is ExecutionContext exec &&
                        exec.RoutingSlip.TryGetExecutedActivity(out var next))
                    {
                        await context.SendAsync(
                            new CompensationContext(
                                exec.Id,
                                next,
                                exec.RoutingSlip));
                    }
                });
        }
    }
}
