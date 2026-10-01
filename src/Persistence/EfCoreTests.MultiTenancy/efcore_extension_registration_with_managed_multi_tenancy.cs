using JasperFx;
using Microsoft.EntityFrameworkCore;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.EntityFrameworkCore.Internals;

namespace EfCoreTests.MultiTenancy;

public class efcore_extension_registration_with_managed_multi_tenancy
{
    private static WolverineOptions configure()
    {
        var options = new WolverineOptions();

        options.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>(
            (builder, connectionString, _) => builder.UseNpgsql(connectionString.Value), AutoCreate.None);
        options.Services.AddDbContextWithWolverineManagedMultiTenancy<OrdersDbContext>(
            (builder, connectionString, _) => builder.UseNpgsql(connectionString.Value), AutoCreate.None);

        // Runs inside UseWolverine(), before the container-registered extensions below
        options.UseEntityFrameworkCoreTransactions();

        new EntityFrameworkCoreBackedPersistence<ItemsDbContext>().Configure(options);
        new EntityFrameworkCoreBackedPersistence<OrdersDbContext>().Configure(options);

        return options;
    }

    [Fact]
    public void the_method_pre_compilation_policies_are_registered_once()
    {
        var policies = configure().CodeGeneration.MethodPreCompilation;

        policies.OfType<EFCoreQuerySpecificationPolicy>().Count().ShouldBe(1);
        policies.OfType<EFCoreBatchingPolicy>().Count().ShouldBe(1);
    }

    [Fact]
    public void the_tenanted_db_context_source_wins_over_service_location()
    {
        var sources = configure().CodeGeneration.Sources;

        sources.First(x => x.Matches(typeof(ItemsDbContext))).ShouldBeOfType<TenantedDbContextSource<ItemsDbContext>>();
        sources.First(x => x.Matches(typeof(OrdersDbContext))).ShouldBeOfType<TenantedDbContextSource<OrdersDbContext>>();
    }
}
