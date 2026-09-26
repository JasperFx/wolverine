using IntegrationTests;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Postgresql;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

public class SoftDeletedItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public bool IsDeleted { get; set; }
    public string? TenantId { get; set; }
}

// ITenanted, but the user declared no query filter of their own. The conjoined
// tenant filter has to be exactly what it always was for this shape
public class UnfilteredTenantedItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? TenantId { get; set; }
}

// NOT ITenanted, but carries the user's own filter -- Wolverine must leave it alone
public class FilteredGlobalThing
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public bool IsDeleted { get; set; }
}

public class SoftDeleteTenancyDbContext : DbContext
{
    public SoftDeleteTenancyDbContext(DbContextOptions<SoftDeleteTenancyDbContext> options) : base(options)
    {
    }

    public DbSet<SoftDeletedItem> SoftItems { get; set; } = null!;
    public DbSet<UnfilteredTenantedItem> UnfilteredItems { get; set; } = null!;
    public DbSet<FilteredGlobalThing> GlobalThings { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SoftDeletedItem>(map =>
        {
            map.ToTable("conjoined_soft_deleted_items", "conjoined");
            map.HasKey(x => x.Id);
            map.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<UnfilteredTenantedItem>(map =>
        {
            map.ToTable("conjoined_unfiltered_items", "conjoined");
            map.HasKey(x => x.Id);
        });

        modelBuilder.Entity<FilteredGlobalThing>(map =>
        {
            map.ToTable("conjoined_filtered_global_things", "conjoined");
            map.HasKey(x => x.Id);
            map.HasQueryFilter(x => !x.IsDeleted);
        });
    }
}

/// <summary>
///     GH-4632. <c>ConjoinedTenancyModelCustomizer.Customize</c> runs after the user's
///     <c>OnModelCreating</c> and called <c>HasQueryFilter(tenantFilter)</c> unconditionally. On EF
///     Core 9 an entity type has exactly one anonymous query filter and the last writer wins, so an
///     <c>ITenanted</c> entity the user had given a soft-delete filter silently lost it: deleted rows
///     came back and nothing warned. EF Core 10 registers the Wolverine filter under a NAME
///     (<c>wolverine_conjoined_tenancy</c>) beside the user's anonymous one, so both apply there --
///     these tests pin that down on both target frameworks.
/// </summary>
[Collection("multi-tenancy")]
public class Bug_4632_conjoined_filter_composition : IAsyncLifetime
{
    private IDbContextBuilder<SoftDeleteTenancyDbContext> theBuilder = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_filters_wolverine");
                opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<SoftDeleteTenancyDbContext>(
                    (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                    AutoCreate.CreateOrUpdate);

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theBuilder = theHost.Services.GetRequiredService<IDbContextBuilder<SoftDeleteTenancyDbContext>>();

        var context = await theBuilder.BuildAsync(CancellationToken.None);
        await context.SoftItems.IgnoreQueryFilters().ExecuteDeleteAsync(CancellationToken.None);
        await context.UnfilteredItems.IgnoreQueryFilters().ExecuteDeleteAsync(CancellationToken.None);
        await context.GlobalThings.IgnoreQueryFilters().ExecuteDeleteAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task conjoined_filter_composes_with_a_user_soft_delete_filter()
    {
        var greenLive = Guid.NewGuid();
        var greenDeleted = Guid.NewGuid();
        var blueLive = Guid.NewGuid();

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        green.SoftItems.Add(new SoftDeletedItem { Id = greenLive, Name = "green live" });
        green.SoftItems.Add(new SoftDeletedItem { Id = greenDeleted, Name = "green deleted", IsDeleted = true });
        await green.SaveChangesAsync(TestContext.Current.CancellationToken);

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        blue.SoftItems.Add(new SoftDeletedItem { Id = blueLive, Name = "blue live" });
        await blue.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reader = await theBuilder.BuildAsync("green", CancellationToken.None);

        // BOTH halves have to survive as real SQL: the user's soft-delete filter AND Wolverine's
        // tenant filter. Asserted against the WHERE clause only -- every column name shows up in
        // the SELECT list, so asserting against the whole statement would pass against the bug
        var sql = reader.SoftItems.ToQueryString();
        sql.ShouldContain("WHERE");
        var whereClause = sql[sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase)..];
        whereClause.ShouldContain(StorageConstants.TenantIdColumn);
        whereClause.ShouldContain(nameof(SoftDeletedItem.IsDeleted));

        var visible = await reader.SoftItems.ToListAsync(TestContext.Current.CancellationToken);
        visible.Select(x => x.Id).ShouldBe([greenLive]);

        // ...and the same through FindAsync, which is how sagas and [Entity] loads read
        (await reader.SoftItems.FindAsync([greenDeleted], TestContext.Current.CancellationToken)).ShouldBeNull();
        (await reader.SoftItems.FindAsync([blueLive], TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>
    ///     The user's filter still has to be escapable on its own terms -- IgnoreQueryFilters()
    ///     drops both, which is exactly what it did before composition
    /// </summary>
    [Fact]
    public async Task ignore_query_filters_still_reaches_everything()
    {
        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        green.SoftItems.Add(new SoftDeletedItem { Id = Guid.NewGuid(), Name = "gone", IsDeleted = true });
        await green.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reader = await theBuilder.BuildAsync("blue", CancellationToken.None);
        (await reader.SoftItems.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(1);
    }

    /// <summary>
    ///     Control: an ITenanted entity the user gave no filter of their own behaves exactly
    ///     as it always has
    /// </summary>
    [Fact]
    public async Task tenanted_entity_with_no_user_filter_is_unchanged()
    {
        var greenId = Guid.NewGuid();
        var blueId = Guid.NewGuid();

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        green.UnfilteredItems.Add(new UnfilteredTenantedItem { Id = greenId, Name = "same" });
        await green.SaveChangesAsync(TestContext.Current.CancellationToken);

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        blue.UnfilteredItems.Add(new UnfilteredTenantedItem { Id = blueId, Name = "same" });
        await blue.SaveChangesAsync(TestContext.Current.CancellationToken);

        var greenReader = await theBuilder.BuildAsync("green", CancellationToken.None);
        (await greenReader.UnfilteredItems.ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Id).ShouldBe([greenId]);

        var blueReader = await theBuilder.BuildAsync("blue", CancellationToken.None);
        (await blueReader.UnfilteredItems.ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Id).ShouldBe([blueId]);
    }

    /// <summary>
    ///     Control: a non-ITenanted entity is untouched -- no tenant_id property, no tenant
    ///     predicate, and the user's own filter intact
    /// </summary>
    [Fact]
    public async Task non_tenanted_entity_with_a_user_filter_is_untouched()
    {
        var live = Guid.NewGuid();

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        green.GlobalThings.Add(new FilteredGlobalThing { Id = live, Name = "live" });
        green.GlobalThings.Add(new FilteredGlobalThing { Id = Guid.NewGuid(), Name = "gone", IsDeleted = true });
        await green.SaveChangesAsync(TestContext.Current.CancellationToken);

        var globalType = green.Model.FindEntityType(typeof(FilteredGlobalThing))!;
        globalType.FindProperty(nameof(SoftDeletedItem.TenantId)).ShouldBeNull();

        // Another tenant's context sees the same global rows -- only the user's filter applies
        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        (await blue.GlobalThings.ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Id).ShouldBe([live]);
    }

    /// <summary>
    ///     The model-level statement of the same thing. The named Wolverine filter that EF 10
    ///     introduced cannot be used here: EF 10 refuses to let an anonymous filter and a named one
    ///     coexist on one entity type ("Both anonymous and named query filters cannot be applied
    ///     simultaneously"), and it throws while the model is being built, which fails the whole
    ///     DbContext at startup. So on BOTH target frameworks an ITenanted entity with the user's
    ///     own anonymous filter ends up with a single composed anonymous filter, and the named
    ///     filter stays the shape used for entities the user left unfiltered.
    /// </summary>
    [Fact]
    public async Task the_model_carries_both_predicates()
    {
        var context = await theBuilder.BuildAsync("green", CancellationToken.None);
        var entityType = context.Model.FindEntityType(typeof(SoftDeletedItem))!;
        var unfilteredType = context.Model.FindEntityType(typeof(UnfilteredTenantedItem))!;

#if NET10_0_OR_GREATER
        var filters = entityType.GetDeclaredQueryFilters().ToArray();
        filters.Length.ShouldBe(1);
        filters.Single().IsAnonymous.ShouldBeTrue();
        var composed = filters.Single().Expression;
        composed.ShouldNotBeNull();
        var text = composed!.ToString();

        // ...while an ITenanted entity with no filter of the user's own still gets the named one
        unfilteredType.GetDeclaredQueryFilters()
            .ShouldContain(x => x.Key == Wolverine.EntityFrameworkCore.Internals.ConjoinedTenancy.QueryFilterName);
#else
        var filter = entityType.GetQueryFilter();
        filter.ShouldNotBeNull();
        var text = filter!.ToString();

        unfilteredType.GetQueryFilter().ShouldNotBeNull();
#endif

        text.ShouldContain(nameof(SoftDeletedItem.IsDeleted));
        text.ShouldContain(nameof(SoftDeletedItem.TenantId));
    }
}
