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

        foreach (var chain in chains.Where(x => !x.HasAttribute<TransactionalAttribute>() && !x.HasAttribute<NonTransactionalAttribute>()))
        {
            if (Idempotency.HasValue)
            {
                chain.Idempotency = Idempotency.Value;
            }
            
            chain.ApplyImpliedMiddlewareFromHandlers(rules);

            // GH-4717: ProvidersClaiming, not a bare CanApply filter. CanApply answers from
            // ServiceDependencies, which walks MethodCall frames only and so cannot see a store this chain
            // reaches solely through [Entity], [All], [FirstOrDefault], [Queryable] or
            // [FromQuerySpecification]. GH-4712 was that gap silently dropping an EF Core user's writes.
            var potentials = rules.OrderedPersistenceProviders().ProvidersClaiming(chain, container);
            if (potentials.Count == 1)
            {
                potentials.Single().ApplyTransactionSupport(chain, container);
                chain.IsTransactional = true;
            }
            else if (potentials.Count > 1 && chain is not SagaChain)
            {
                // GH-4631. Two providers can own this chain's transaction -- a handler taking both a
                // DbContext and an IDocumentSession, say. Applying none of them, which is what this
                // policy used to do, is silent data loss: neither store is ever told to save. A saga
                // chain is excluded because SagaChain.DetermineFrames resolves its own provider
                // deterministically from the saga's own state storage.
                var owner = TransactionOwnerResolution.SelectDesignatedOwner(chain, potentials, container);
                owner.ApplyTransactionSupport(chain, container);
                chain.IsTransactional = true;
            }
        }
    }

    public IdempotencyStyle? Idempotency { get; set; }
}