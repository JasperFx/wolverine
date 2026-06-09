using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy;

/// <summary>
/// Regression guard for the managed-multi-tenancy counterpart of the single-DbContext domain-event
/// scraper tests in <c>EfCoreTests/DomainEvents/configuration_of_domain_events_scrapers.cs</c>.
///
/// The EF Core transactional middleware has two codegen paths: the single-DbContext path commits
/// through <c>EfCoreEnvelopeTransaction.CommitAsync()</c>, which runs the registered
/// <c>IDomainEventScraper</c>s; the managed-multi-tenancy path
/// (<c>StartDatabaseTransactionForDbContext</c> / <c>CommitTenantedDbContextTransaction</c>) used to
/// commit the raw EF transaction directly and never invoked <c>CommitAsync()</c>, so the scrapers
/// registered by <c>PublishDomainEventsFromEntityFrameworkCore&lt;T&gt;(x =&gt; x.Events)</c> never ran.
/// A mutated entity's domain events were therefore silently dropped under managed multi-tenancy.
///
/// This test mutates an <see cref="Item"/> in a handler under a tenant so the entity publishes an
/// <see cref="ItemApproved"/> domain event, then asserts (via a Wolverine tracked session) that the
/// event was scraped and published.
/// </summary>
public class domain_events_scraping_with_managed_multi_tenancy : MultiTenancyCompliance
{
    public domain_events_scraping_with_managed_multi_tenancy() : base(DatabaseEngine.PostgreSQL)
    {
    }

    public override void Configure(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "domain_event_scraping_mt")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        // Little weird, but we have to remove this DbContext to use
        // the lightweight saga persistence
        opts.Services.RemoveAll(typeof(OrdersDbContext));
        opts.AddSagaType<Order>();

        opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
        {
            builder.UseNpgsql(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
        }, AutoCreate.CreateOrUpdate);

        // The behavior under test: scrape domain events off entities tracked by the tenant DbContext.
        opts.PublishDomainEventsFromEntityFrameworkCore<IEntity, IDomainEvent>(x => x.Events);

        // The approval handler + ItemApproved handler live in this test assembly.
        opts.Discovery.IncludeAssembly(GetType().Assembly);
    }

    [Fact]
    public async Task publishes_domain_events_scraped_from_the_tenant_db_context()
    {
        var itemId = Guid.NewGuid();

        // Seed the item in the "blue" tenant database.
        await theHost.InvokeMessageAndWaitAsync(new StartNewItem(itemId, "Latte"), "blue");

        // Approve it under the same tenant - the handler calls Item.Approve(), which publishes an
        // ItemApproved domain event into the entity's Events collection. The scraper must relay it.
        var tracked = await theHost.InvokeMessageAndWaitAsync(new ApproveTenantedItem(itemId), "blue");

        tracked.Sent.SingleMessage<ItemApproved>().Id.ShouldBe(itemId);
    }
}

public record ApproveTenantedItem(Guid Id);

public static class ApproveTenantedItemHandler
{
    // Taking the ItemsDbContext (via [Entity]) applies the EF Core transactional middleware so the
    // domain-event scrape path is exercised on commit.
    public static IStorageAction<Item> Handle(ApproveTenantedItem command, [Entity] Item item)
    {
        // Publishes an ItemApproved event internally on the Item entity that we want relayed to Wolverine.
        item.Approve();
        return Storage.Update(item);
    }

    public static void Handle(ItemApproved e) => Debug.WriteLine($"Got item approved for {e.Id}");
}
