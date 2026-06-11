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
    /// Configures the default or main DbContext type to resolve conflicts when multiple DbContext types
    /// are detected in a message handler chain.
    /// </summary>
    /// <typeparam name="TDbContext">The main DbContext type</typeparam>
    public EFCoreTransactionConfiguration UseMainDbContext<TDbContext>() where TDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        _provider.MainDbContextType = typeof(TDbContext);
        return this;
    }
}
