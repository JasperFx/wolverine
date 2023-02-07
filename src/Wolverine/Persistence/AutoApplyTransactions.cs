using JasperFx.CodeGeneration;
using Lamar;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Persistence;

internal class AutoApplyTransactions : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IContainer container)
    {
        var providers = rules.PersistenceProviders();
        if (!providers.Any()) return;
        
        foreach (var chain in chains.Where(x => !x.HasAttribute<TransactionalAttribute>()))
        {
            var potentials = providers.Where(x => x.CanApply(chain, container)).ToArray();
            if (potentials.Length == 1)
            {
                potentials.Single().ApplyTransactionSupport(chain, container);
            }
        }
    }
}