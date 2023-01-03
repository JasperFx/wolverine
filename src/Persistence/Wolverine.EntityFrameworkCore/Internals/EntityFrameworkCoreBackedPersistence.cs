using Microsoft.Extensions.DependencyInjection;
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
        options.Node.CodeGeneration.AddPersistenceStrategy<EFCorePersistenceFrameProvider>();

        options.Services.AddScoped(typeof(IDbContextOutbox<>), typeof(DbContextOutbox<>));
        options.Services.AddScoped<IDbContextOutbox, DbContextOutbox>();
    }
}