using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence.Sagas;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     Add to your Wolverine application to opt into EF Core-backed
///     transaction and saga persistence middleware.
///     Warning! This has to be used in conjunction with a Wolverine
///     database package
/// </summary>
internal class EntityFrameworkCoreBackedPersistence : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.CodeGeneration.InsertFirstPersistenceStrategy<EFCorePersistenceFrameProvider>();
    }
}

/// <summary>
///     Add to your Wolverine application to opt into EF Core-backed
///     transaction and saga persistence middleware.
///     Warning! This has to be used in conjunction with a Wolverine
///     database package
/// </summary>
internal class EntityFrameworkCoreBackedPersistence<T> : IWolverineExtension where T : DbContext
{
    public void Configure(WolverineOptions options)
    {
        options.CodeGeneration.InsertFirstPersistenceStrategy<EFCorePersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new TenantedDbContextSource<T>());
    }
}