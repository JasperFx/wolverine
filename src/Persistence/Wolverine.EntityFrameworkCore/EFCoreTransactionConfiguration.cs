using System;
using System.Linq;
using System.Reflection;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore.Codegen;

namespace Wolverine.EntityFrameworkCore;

public class EFCoreTransactionConfiguration
{
    private readonly WolverineOptions _options;
    private readonly EFCorePersistenceFrameProvider _provider;

    internal EFCoreTransactionConfiguration(WolverineOptions options, EFCorePersistenceFrameProvider provider)
    {
        _options = options;
        _provider = provider;
    }

    /// <summary>
    /// Register a DbContext abstraction that should be used for auto-transactions
    /// when the abstraction is used as a dependency in a handler.
    /// </summary>
    /// <typeparam name="TAbstraction">The abstraction type (e.g., IUnitOfWork)</typeparam>
    /// <typeparam name="TDbContext">The concrete DbContext type</typeparam>
    public EFCoreTransactionConfiguration WithDbContextAbstraction<TAbstraction, TDbContext>() where TDbContext : Microsoft.EntityFrameworkCore.DbContext, TAbstraction
    {
        _provider.RegisterAbstraction(typeof(TAbstraction), typeof(TDbContext));
        _options.CodeGeneration.AlwaysUseServiceLocationFor<TAbstraction>();

        return this;
    }

    /// <summary>
    /// Select, at registration time, which DbContext is the transactional one for handlers that
    /// depend on more than one DbContext-shaped service (e.g. one write context plus a read-only
    /// lookup or another module's context). This is the Clean Architecture / modular-monolith path:
    /// the handler needs no [Transactional] attribute and no reference to any DbContext type.
    /// <para>
    /// <typeparamref name="TDbContext"/> may be a concrete DbContext or a type registered via
    /// <see cref="WithDbContextAbstraction{TAbstraction,TDbContext}"/>. By default the selection
    /// applies to every chain that depends on that context; pass <paramref name="appliesTo"/> to
    /// scope it (for example to a single module's handlers).
    /// </para>
    /// </summary>
    /// <param name="appliesTo">Optional predicate restricting which handler chains this selection applies to.</param>
    public EFCoreTransactionConfiguration WithTransactionalDbContext<TDbContext>(Func<IChain, bool>? appliesTo = null)
    {
        _provider.RegisterTransactionalSelection(typeof(TDbContext), appliesTo);
        return this;
    }

    /// <summary>
    /// Select the transactional DbContext for every handler defined in <paramref name="forHandlersIn"/>.
    /// The natural modular-monolith form: each module declares its own write context for its own
    /// handler assembly, so a handler that writes its module's context while reading another module's
    /// context enrolls only the former.
    /// </summary>
    /// <param name="forHandlersIn">The module/handler assembly this selection applies to.</param>
    public EFCoreTransactionConfiguration WithTransactionalDbContext<TDbContext>(Assembly forHandlersIn)
    {
        return WithTransactionalDbContext<TDbContext>(
            chain => chain.HandlerCalls().Any(call => call.HandlerType.Assembly == forHandlersIn));
    }
}
