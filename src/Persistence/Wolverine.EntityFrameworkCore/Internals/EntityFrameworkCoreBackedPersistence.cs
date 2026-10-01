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

        AddMethodPreCompilationPolicies(options);

        AddFactoryRefusalPolicy(options);

        // The CritterWatch / saga-explorer ISagaStoreDiagnostics fan-out registration
        // lives in WolverineEntityCoreExtensions.registerEFCoreSagaStoreDiagnostics
        // (called from every entry point that registers this extension). Registering
        // it here would tear at the IServiceCollection after host-build because this
        // extension is itself registered into DI, which trips Wolverine's 3.0+ "no
        // IoC mods from container-registered extensions" policy. Closes wolverine#2735.
    }

    /// <summary>
    /// EFCoreQuerySpecificationPolicy detects IQueryPlan&lt;TDbContext,TResult&gt;-typed variables produced by
    /// Load/LoadAsync methods and injects FetchSpecificationFrames to execute them. It must run BEFORE
    /// EFCoreBatchingPolicy so those injected frames (IEFCoreBatchableFrame) are grouped into a single
    /// BatchedQuery round-trip.
    /// </summary>
    /// <remarks>
    /// Idempotent, because every entry point registers its own extension: the non-generic one from
    /// <c>UseEntityFrameworkCoreTransactions</c> and <c>AddDbContextWithWolverineIntegration</c>, and one
    /// generic one per DbContext from the multi-tenancy registrations. Each policy is method-wide rather than
    /// per-DbContext, so a second copy is never more coverage -- the query plan policy would inject a second
    /// fetch for every plan, and the generated method would declare the plan's result variable twice.
    /// </remarks>
    internal static void AddMethodPreCompilationPolicies(WolverineOptions options)
    {
        var policies = options.CodeGeneration.MethodPreCompilation;
        if (policies.OfType<EFCoreQuerySpecificationPolicy>().Any()) return;

        policies.Add(new EFCoreQuerySpecificationPolicy());
        policies.Add(new EFCoreBatchingPolicy());
    }

    /// <summary>
    /// GH-4635. Registered here rather than from <c>UseEntityFrameworkCoreTransactions</c> so that the
    /// <c>AddDbContextWithWolverineIntegration</c>-only bootstrap (which never calls it) is covered too.
    /// Idempotent -- both entry points route through this one extension, but an app can register the
    /// generic and non-generic flavors together.
    /// </summary>
    internal static void AddFactoryRefusalPolicy(WolverineOptions options)
    {
        if (options.Policies.OfType<DbContextFactoryRefusalPolicy>().Any()) return;

        options.Policies.Add(new DbContextFactoryRefusalPolicy());
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
        // Inserted first, not appended. The first matching source wins, and UseEntityFrameworkCoreTransactions()
        // appends a service-location source for every DbContext already registered -- which includes T, because
        // the multi-tenancy registrations add a scoped T for EF Core migrations. That call runs inside the
        // UseWolverine() callback, before this extension is applied from the container, so appending here
        // let it win: any chain that did not build the DbContext through the transactional middleware
        // resolved the main database's DbContext from the container instead of the request tenant's.
        options.CodeGeneration.Sources.Insert(0, new TenantedDbContextSource<T>());

        if (ConjoinedTenancy.IsConjoined(typeof(T)))
        {
            // Conjoined tenancy keeps its authoritative tenant list in the message
            // store's wolverine_tenants registry table
            options.Durability.TenantRegistryRequired = true;
        }

        EntityFrameworkCoreBackedPersistence.AddMethodPreCompilationPolicies(options);

        EntityFrameworkCoreBackedPersistence.AddFactoryRefusalPolicy(options);

        // Auto-allow this DbContext type for service location. EF Core's
        // multi-tenancy registration paths register the DbContext via opaque
        // factories (TenantedDbContextBuilderByConnectionString / ByDbDataSource),
        // which Wolverine codegen can't see through. Without this, every handler
        // that takes the DbContext as a parameter would fail under Wolverine 6.0's
        // ServiceLocationPolicy.NotAllowed default. Touches options.CodeGeneration
        // (in-memory), not options.Services — safe to do from Configure even when
        // this extension is run from the post-host-build resolution path. See
        // sibling auto-allow in WolverineEntityCoreExtensions.UseEntityFrameworkCoreTransactions
        // for the non-multi-tenancy path.
        options.CodeGeneration.AlwaysUseServiceLocationFor<T>();

        // The CritterWatch / saga-explorer ISagaStoreDiagnostics fan-out registration
        // lives in WolverineEntityCoreExtensions.registerEFCoreSagaStoreDiagnostics
        // (called from every entry point that registers this extension). Registering
        // it here would tear at the IServiceCollection after host-build because this
        // extension is itself registered into DI, which trips Wolverine's 3.0+ "no
        // IoC mods from container-registered extensions" policy. Closes wolverine#2735.
    }
}