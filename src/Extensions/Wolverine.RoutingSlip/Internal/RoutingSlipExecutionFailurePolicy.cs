using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Wolverine.RoutingSlip.Internal;

/// <summary>
///     Applies either the user-provided routing slip failure policy or the default discard + compensation behavior
///     to handler chains that process <see cref="ExecutionContext" /> and <see cref="CompensationContext" /> messages.
/// </summary>
/// <param name="routingSlipOptions"></param>
internal sealed class RoutingSlipExecutionFailurePolicy(RoutingSlipOptions routingSlipOptions) : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, JasperFx.IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            if (chain.MessageType != typeof(ExecutionContext) &&
                chain.MessageType != typeof(CompensationContext))
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
                .And(async (_, context, exception) =>
                {
                    var message = context.Envelope?.Message;
                    var exceptionInfo = ExceptionInfo.From(exception);
                    switch (message)
                    {
                        case ExecutionContext execution when execution.RoutingSlip.TryGetExecutedActivity(out var next):
                            await context.PublishAsync(new RoutingSlipActivityFaulted(
                                execution.RoutingSlip.TrackingNumber,
                                exceptionInfo));

                            await context.PublishAsync(new CompensationContext(
                                execution.Id,
                                next,
                                execution.RoutingSlip));
                            break;

                        case CompensationContext compensation:
                            await context.PublishAsync(new RoutingSlipCompensationFailed(
                                compensation.RoutingSlip.TrackingNumber,
                                exceptionInfo));
                            break;
                    }
                });
        }
    }
}
