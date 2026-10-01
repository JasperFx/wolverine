using JasperFx;
using Microsoft.EntityFrameworkCore;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;

namespace EfCoreTests.MultiTenancy;

/// <summary>
/// Managed multi-tenancy registers one <c>EntityFrameworkCoreBackedPersistence&lt;T&gt;</c> extension per
/// DbContext, from the container, AFTER the <c>UseWolverine()</c> callback -- and
/// <c>UseEntityFrameworkCoreTransactions()</c> registers the non-generic one from inside it. Everything those
/// extensions add to the code generation rules has to come out right regardless of how many of them run, and in
/// which order. The end-to-end symptoms are covered over HTTP in
/// <c>Wolverine.Http.Tests.MultiTenancy.entity_on_step_methods_with_managed_multi_tenancy</c>.
/// </summary>
public class efcore_extension_registration_with_managed_multi_tenancy
{
    private static WolverineOptions configure()
    {
        var options = new WolverineOptions();

        options.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>(
            (builder, connectionString, _) => builder.UseNpgsql(connectionString.Value), AutoCreate.None);
        options.Services.AddDbContextWithWolverineManagedMultiTenancy<OrdersDbContext>(
            (builder, connectionString, _) => builder.UseNpgsql(connectionString.Value), AutoCreate.None);

        // Inside the UseWolverine() callback, so it runs first
        options.UseEntityFrameworkCoreTransactions();

        // What WolverineOptions does later with the extensions the registrations above put in the container
        new EntityFrameworkCoreBackedPersistence<ItemsDbContext>().Configure(options);
        new EntityFrameworkCoreBackedPersistence<OrdersDbContext>().Configure(options);

        return options;
    }

    [Fact]
    public void the_tenanted_db_context_source_wins_over_service_location()
    {
        // The first matching source wins. UseEntityFrameworkCoreTransactions() appends a service-location
        // source for every registered DbContext, and the multi-tenancy registration puts a scoped one in the
        // container for EF Core migrations -- a DbContext for the MAIN database. Appended after it, the
        // tenanted source never got the chance to build the request tenant's.
        var sources = configure().CodeGeneration.Sources;

        sources.First(x => x.Matches(typeof(ItemsDbContext))).ShouldBeOfType<TenantedDbContextSource<ItemsDbContext>>();
        sources.First(x => x.Matches(typeof(OrdersDbContext))).ShouldBeOfType<TenantedDbContextSource<OrdersDbContext>>();
    }
}
