using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

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

        // EFCoreQuerySpecificationPolicy detects IQueryPlan<TDbContext,TResult>-typed
        // variables produced by Load/LoadAsync methods and injects FetchSpecificationFrames
        // to execute them. Must run BEFORE EFCoreBatchingPolicy so those injected frames
        // (IEFCoreBatchableFrame) are grouped into a single BatchedQuery round-trip.
        options.CodeGeneration.MethodPreCompilation.Add(new EFCoreQuerySpecificationPolicy());
        options.CodeGeneration.MethodPreCompilation.Add(new EFCoreBatchingPolicy());

        // The CritterWatch / saga-explorer ISagaStoreDiagnostics fan-out registration
        // lives in WolverineEntityCoreExtensions.registerEFCoreSagaStoreDiagnostics
        // (called from every entry point that registers this extension). Registering
        // it here would tear at the IServiceCollection after host-build because this
        // extension is itself registered into DI, which trips Wolverine's 3.0+ "no
        // IoC mods from container-registered extensions" policy. Closes wolverine#2735.
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
        options.CodeGeneration.ReferenceAssembly(GetType().Assembly);
        options.CodeGeneration.InsertFirstPersistenceStrategy<EFCorePersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new TenantedDbContextSource<T>());

        options.CodeGeneration.MethodPreCompilation.Add(new EFCoreQuerySpecificationPolicy());
        options.CodeGeneration.MethodPreCompilation.Add(new EFCoreBatchingPolicy());

        // The CritterWatch / saga-explorer ISagaStoreDiagnostics fan-out registration
        // lives in WolverineEntityCoreExtensions.registerEFCoreSagaStoreDiagnostics
        // (called from every entry point that registers this extension). Registering
        // it here would tear at the IServiceCollection after host-build because this
        // extension is itself registered into DI, which trips Wolverine's 3.0+ "no
        // IoC mods from container-registered extensions" policy. Closes wolverine#2735.
    }
}