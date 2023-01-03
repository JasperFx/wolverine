using System.Linq;
using JasperFx.CodeGeneration;
using Lamar;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

internal class AutoApplyTransactions : IHandlerPolicy
{
    public void Apply(HandlerGraph graph, GenerationRules rules, IContainer container)
    {
        var providers = rules.PersistenceProviders();
        if (!providers.Any()) return;
        
        foreach (var chain in graph.Chains.Where(x => !x.HasAttribute<TransactionalAttribute>()))
        {
            var potentials = providers.Where(x => x.CanApply(chain, container)).ToArray();
            if (potentials.Length == 1)
            {
                potentials.Single().ApplyTransactionSupport(chain, container);
            }
        }
    }
}