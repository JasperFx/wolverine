using System.Linq;
using JasperFx.CodeGeneration;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

internal class MartenAggregateHandlerStrategy : IHandlerPolicy
{
    public void Apply(HandlerGraph graph, GenerationRules rules, IContainer container)
    {
        foreach (var chain in graph.Chains.Where(x => x.Handlers.Any(call => call.HandlerType.Name.EndsWith("AggregateHandler"))))
        {
            if (chain.HasAttribute<MartenCommandWorkflowAttribute>())
            {
                continue;
            }
            
            new MartenCommandWorkflowAttribute(ConcurrencyStyle.Optimistic).Modify(chain, rules, container);
        }
    }
}