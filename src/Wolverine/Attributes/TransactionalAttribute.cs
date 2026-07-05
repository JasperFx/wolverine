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
    /// <summary>
    /// Chain tag key carrying the persistence storage type (e.g. a specific EF Core DbContext)
    /// that a <see cref="TransactionalAttribute"/> designates as the transactional one. Read by
    /// persistence providers to disambiguate multi-storage handler chains.
    /// </summary>
    public const string TransactionalDbContextTypeKey = "TransactionalDbContextType";

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

        if (DbContextType != null)
        {
            // Consumed by the persistence provider (e.g. EF Core's
            // EFCorePersistenceFrameProvider) to disambiguate which storage type owns the
            // transaction when a chain depends on more than one. Kept as a chain tag so this
            // attribute — and the core Wolverine assembly — never reference EF Core.
            chain.Tags[TransactionalDbContextTypeKey] = DbContextType;
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

    /// <summary>
    /// Returns true if <see cref="Mode"/> was explicitly set on this attribute instance.
    /// Used by persistence providers to resolve the effective mode even before
    /// <see cref="Modify"/> has been called (e.g. when side effects are processed at startup).
    /// </summary>
    public bool IsModeExplicitlySet => _modeExplicitlySet;

    /// <summary>
    /// Optionally designate which persistence storage type owns the transaction for this handler
    /// when its chain depends on more than one candidate (for example two EF Core DbContext types —
    /// one written through, one read-only). This may be the concrete type or an abstraction the
    /// provider has been taught to resolve (e.g. a DbContext abstraction registered via
    /// <c>WithDbContextAbstraction&lt;TAbstraction, TDbContext&gt;()</c>). It is only consulted when a
    /// chain is otherwise ambiguous; single-candidate handlers ignore it. The value is carried as a
    /// plain <see cref="Type"/>, so neither this attribute nor the core Wolverine assembly reference
    /// any specific persistence technology.
    /// </summary>
    public Type? DbContextType { get; set; }

    public TransactionalAttribute()
    {
    }

    public TransactionalAttribute(IdempotencyStyle idempotency)
    {
        Idempotency = idempotency;
    }

    /// <summary>
    /// Designate which persistence storage type (e.g. a specific EF Core DbContext, or a registered
    /// DbContext abstraction) owns the transaction for this handler, for chains that depend on more
    /// than one candidate. See <see cref="DbContextType"/>.
    /// </summary>
    public TransactionalAttribute(Type dbContextType)
    {
        DbContextType = dbContextType;
    }
}