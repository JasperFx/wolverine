using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

internal class MartenAggregateHandlerStrategy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(x =>
                     x.Handlers.Any(call => call.HandlerType.Name.EndsWith("AggregateHandler"))))
        {
            if (chain.HasAttribute<AggregateHandlerAttribute>())
            {
                continue;
            }

            if (chain.Handlers.SelectMany(x => x.Creates).Any(x => x.VariableType.CanBeCastTo<IStartStream>())) continue;

            new AggregateHandlerAttribute(ConcurrencyStyle.Optimistic).Modify(chain, rules, container);
        }
    }
}