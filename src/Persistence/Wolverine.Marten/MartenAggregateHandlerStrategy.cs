using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

internal class MartenAggregateHandlerStrategy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        ILogger? logger = null;

        foreach (var chain in chains.Where(x =>
                     x.Handlers.Any(call => call.HandlerType.Name.EndsWith("AggregateHandler"))))
        {
            if (chain.HasAttribute<AggregateHandlerAttribute>())
            {
                continue;
            }

            if (chain.Handlers.SelectMany(x => x.Creates).Any(x => x.VariableType.CanBeCastTo<IStartStream>())) continue;

            // GH-2922: this chain is being promoted into the Marten aggregate event-sourcing workflow
            // purely because its handler type name ends with "AggregateHandler" (there is no explicit
            // [AggregateHandler] attribute). In that workflow the handler's return value(s) are appended
            // to the aggregate's event stream rather than published as cascading messages. If the handler
            // also declares a [ReadAggregate] parameter (read-only intent), the author very likely meant
            // the return value to be published as a message, so warn rather than silently turning it into
            // an event.
            if (UsesReadAggregate(chain))
            {
                logger ??= container.Services.GetService<ILoggerFactory>()?.CreateLogger<MartenAggregateHandlerStrategy>()
                    ?? NullLogger<MartenAggregateHandlerStrategy>.Instance;

                logger.LogWarning(
                    "Handler {HandlerType} is being treated as a Marten aggregate handler because its type name ends with \"AggregateHandler\", so its return value is appended to the aggregate's event stream instead of being published as a cascading message. The handler also declares a [ReadAggregate] parameter, which suggests read-only intent. If you meant to publish the return value as a message, rename the handler type so it does not end in \"AggregateHandler\". See https://wolverinefx.net/guide/durability/marten/event-sourcing (GH-2922).",
                    chain.Handlers.First().HandlerType.FullNameInCode());
            }

            new AggregateHandlerAttribute(ConcurrencyStyle.Optimistic).Modify(chain, rules, container);
        }
    }

    private static bool UsesReadAggregate(HandlerChain chain)
    {
        return chain.Handlers.Any(call =>
            call.Method.GetParameters().Any(p => p.GetCustomAttribute<ReadAggregateAttribute>() != null));
    }
}
