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
    /// Sets the default <see cref="System.Data.IsolationLevel"/> for the explicit database transaction
    /// the Eager transactional middleware opens. Without this, the database provider chooses. A
    /// handler can override it with <c>[Transactional(IsolationLevel = ...)]</c>. A transaction that
    /// already exists is reused as is, and Lightweight mode opens none, so neither is affected.
    /// </summary>
    public EFCoreTransactionConfiguration WithIsolationLevel(System.Data.IsolationLevel isolationLevel)
    {
        _provider.DefaultIsolationLevel = isolationLevel;
        return this;
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
}
