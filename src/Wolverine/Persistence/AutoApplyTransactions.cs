using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

internal class AutoApplyTransactions : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var providers = rules.PersistenceProviders();
        if (providers.Count == 0)
        {
            return;
        }

        foreach (var chain in chains.Where(x => !x.HasAttribute<TransactionalAttribute>()))
        {
            chain.ApplyImpliedMiddlewareFromHandlers(rules);
            var potentials = providers.Where(x => x.CanApply(chain, container)).ToArray();
            if (potentials.Length == 1)
            {
                potentials.Single().ApplyTransactionSupport(chain, container);
            }
        }
    }
}