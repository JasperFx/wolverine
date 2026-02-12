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
    private bool _modeExplicitlySet;

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        if (Idempotency.HasValue)
        {
            chain.Idempotency = Idempotency.Value;
        }

        if (_modeExplicitlySet)
        {
            chain.Tags["TransactionMiddlewareMode"] = Mode;
        }

        chain.ApplyImpliedMiddlewareFromHandlers(rules);
        var transactionFrameProvider = rules.As<GenerationRules>().GetPersistenceProviders(chain, container);
        transactionFrameProvider.ApplyTransactionSupport(chain, container);

        chain.IsTransactional = true;
    }

    public IdempotencyStyle? Idempotency { get; set; }

    /// <summary>
    /// Optionally override the <see cref="TransactionMiddlewareMode"/> for just this handler chain.
    /// When set, this takes precedence over the global mode configured in
    /// <c>UseEntityFrameworkCoreTransactions()</c>.
    /// </summary>
    public TransactionMiddlewareMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            _modeExplicitlySet = true;
        }
    }
    private TransactionMiddlewareMode _mode;

    public TransactionalAttribute()
    {
    }

    public TransactionalAttribute(IdempotencyStyle idempotency)
    {
        Idempotency = idempotency;
    }
}