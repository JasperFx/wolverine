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
    public EFCoreTransactionConfiguration WithDbContextAbstraction<TAbstraction, TDbContext>() where TDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        _provider.RegisterAbstraction(typeof(TAbstraction), typeof(TDbContext));
        _options.CodeGeneration.AlwaysUseServiceLocationFor<TAbstraction>();

        return this;
    }
}
