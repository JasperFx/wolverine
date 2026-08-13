using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests;

// The EF Core proof for [All] and [Queryable]. No IEventStoreOperations coverage here -- EF Core is not an
// event store, and its provider deliberately does not implement that seam.
[Collection("sqlserver")]
public class all_and_queryable_attributes : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EfWidgetHandler));

                opts.Services.AddDbContextWithWolverineIntegration<EfWidgetCatalogDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "all_queryable");
                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EfWidgetCatalogDbContext>();
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        db.Widgets.RemoveRange(db.Widgets);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task seed()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EfWidgetCatalogDbContext>();
        await db.Widgets.AddRangeAsync(
        [
            new EfWidget { Id = Guid.NewGuid(), Name = "red", Hits = 5 },
            new EfWidget { Id = Guid.NewGuid(), Name = "green", Hits = 12 },
            new EfWidget { Id = Guid.NewGuid(), Name = "blue", Hits = 3 }
        ], TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task all_gives_an_empty_list_when_nothing_is_stored()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new CountEfWidgets());
        tracked.Sent.SingleMessage<EfWidgetsCounted>().Count.ShouldBe(0);
    }

    [Fact]
    public async Task all_supplies_every_row()
    {
        await seed();
        var tracked = await _host.InvokeMessageAndWaitAsync(new CountEfWidgets());
        tracked.Sent.SingleMessage<EfWidgetsCounted>().Count.ShouldBe(3);
    }

    [Fact]
    public async Task queryable_can_be_composed_against()
    {
        await seed();
        var tracked = await _host.InvokeMessageAndWaitAsync(new FindPopularEfWidgets(4));
        tracked.Sent.SingleMessage<PopularEfWidgetsFound>().Names.ShouldBe(["green", "red"]);
    }
}

public class EfWidget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public int Hits { get; set; }
}

public class EfWidgetCatalogDbContext : DbContext
{
    public EfWidgetCatalogDbContext(DbContextOptions<EfWidgetCatalogDbContext> options) : base(options) { }

    public DbSet<EfWidget> Widgets { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.MapWolverineEnvelopeStorage();
        modelBuilder.Entity<EfWidget>(map =>
        {
            map.ToTable("ef_widgets");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
            map.Property(x => x.Hits);
        });
    }
}

public record CountEfWidgets;
public record FindPopularEfWidgets(int Minimum);
public record EfWidgetsCounted(int Count);
public record PopularEfWidgetsFound(string[] Names);

[WolverineIgnore]
public static class EfWidgetHandler
{
    public static EfWidgetsCounted Handle(CountEfWidgets command, [All] IReadOnlyList<EfWidget> widgets)
        => new(widgets.Count);

    // Async LINQ only, per the [Queryable] guidance -- EF Core would tolerate the sync form, Marten would not
    public static async Task<PopularEfWidgetsFound> Handle(FindPopularEfWidgets command,
        [Queryable] IQueryable<EfWidget> widgets, CancellationToken token)
    {
        var names = await widgets.Where(x => x.Hits >= command.Minimum)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Name)
            .ToListAsync(token);

        return new PopularEfWidgetsFound(names.ToArray());
    }

    public static void Handle(EfWidgetsCounted msg) { }
    public static void Handle(PopularEfWidgetsFound msg) { }
}
