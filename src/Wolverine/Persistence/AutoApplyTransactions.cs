using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;

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
            if (Idempotency.HasValue)
            {
                chain.Idempotency = Idempotency.Value;
            }
            
            chain.ApplyImpliedMiddlewareFromHandlers(rules);
            var potentials = providers.Where(x => x.CanApply(chain, container)).ToArray();
            if (potentials.Length == 1)
            {
                potentials.Single().ApplyTransactionSupport(chain, container);
            }
        }
    }

    public IdempotencyStyle? Idempotency { get; set; }
}