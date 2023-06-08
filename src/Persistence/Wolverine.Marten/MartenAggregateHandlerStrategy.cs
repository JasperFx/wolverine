using System.Runtime.Intrinsics.X86;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

internal class MartenAggregateHandlerStrategy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains.Where(x =>
                     x.Handlers.Any(call => call.HandlerType.Name.EndsWith("AggregateHandler"))))
        {
            if (chain.HasAttribute<AggregateHandlerAttribute>())
            {
                continue;
            }
            
            if (chain.Handlers.SelectMany(x => x.Creates).Any(x => x.VariableType.CanBeCastTo<StartStream>())) continue;

            new AggregateHandlerAttribute(ConcurrencyStyle.Optimistic).Modify(chain, rules, container);
        }
    }
}