using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Attributes;

/// <summary>
///     Applies unit of work / transactional boundary middleware to the
///     current chain using the currently configured persistence
/// </summary>
public class TransactionalAttribute : ModifyChainAttribute
{
    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        if (Idempotency.HasValue)
        {
            chain.Idempotency = Idempotency.Value;
        }
        
        chain.ApplyImpliedMiddlewareFromHandlers(rules);
        var transactionFrameProvider = rules.As<GenerationRules>().GetPersistenceProviders(chain, container);
        transactionFrameProvider.ApplyTransactionSupport(chain, container);
    }
    
    public IdempotencyStyle? Idempotency { get; set; }

    public TransactionalAttribute()
    {
    }

    public TransactionalAttribute(IdempotencyStyle idempotency)
    {
        Idempotency = idempotency;
    }
}