using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence.Sagas;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
///     Add to your Wolverine application to opt into EF Core-backed
///     transaction and saga persistence middleware.
///     Warning! This has to be used in conjunction with a Wolverine
///     database package
/// </summary>
public class EntityFrameworkCoreBackedPersistence : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        var frameProvider = new EFCorePersistenceFrameProvider();
        options.Advanced.CodeGeneration.SetSagaPersistence(frameProvider);
        options.Advanced.CodeGeneration.SetTransactions(frameProvider);
    }
}
