using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Weasel.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace EfCoreTests;

/// <summary>
/// Demonstrates IInitialData&lt;TContext&gt; for FK-safe seed data applied after
/// IDatabaseCleaner&lt;TContext&gt;.ResetAllDataAsync().
/// </summary>
public class SeedItemsForTests : IInitialData<ItemsDbContext>
{
    public static readonly Item[] Items =
    [
        new Item { Id = Guid.Parse("10000000-0000-0000-0000-000000000001"), Name = "Alpha Item" },
        new Item { Id = Guid.Parse("10000000-0000-0000-0000-000000000002"), Name = "Beta Item" }
    ];

    public async Task Populate(ItemsDbContext context, CancellationToken cancellation)
    {
        context.Items.AddRange(Items);
        await context.SaveChangesAsync(cancellation);
    }
}

/// <summary>
/// Test fixture that registers IDatabaseCleaner&lt;ItemsDbContext&gt; and IInitialData&lt;ItemsDbContext&gt;
/// alongside Wolverine's normal setup.
/// </summary>
public class DatabaseCleanerContext : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddDbContext<ItemsDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));

                // Registers IDatabaseCleaner<ItemsDbContext> as a singleton.
                // It discovers tables from DbContext.Model, topologically sorts them by
                // FK dependencies, and generates provider-specific deletion SQL
                // (TRUNCATE CASCADE for PostgreSQL, DELETE FROM for SQL Server).
                services.AddDatabaseCleaner<ItemsDbContext>();

                // Registers seed data applied after ResetAllDataAsync().
                // Multiple IInitialData<T> registrations execute in order.
                services.AddInitialData<ItemsDbContext, SeedItemsForTests>();
            })
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opts.UseEntityFrameworkCoreTransactions();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

/// <summary>
/// Integration tests demonstrating Weasel's IDatabaseCleaner&lt;TContext&gt; as the
/// recommended pattern for FK-aware database reset in EF Core integration tests.
/// Replaces manual DELETE FROM / TRUNCATE / schema-drop cleanup code.
/// </summary>
[Collection("sqlserver")]
public class database_cleaner_usage_tests : IClassFixture<DatabaseCleanerContext>
{
    private readonly DatabaseCleanerContext _ctx;

    public database_cleaner_usage_tests(DatabaseCleanerContext ctx)
    {
        _ctx = ctx;
    }

    private IDatabaseCleaner<ItemsDbContext> Cleaner =>
        _ctx.Host.Services.GetRequiredService<IDatabaseCleaner<ItemsDbContext>>();

    private async Task<int> CountItemsAsync()
    {
        using var scope = _ctx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
        return await db.Items.CountAsync();
    }

    [Fact]
    public async Task delete_all_data_removes_every_row()
    {
        // Arrange: insert some extra items so the table is non-empty
        using (var scope = _ctx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            db.Items.Add(new Item { Id = Guid.NewGuid(), Name = "Temp Item" });
            await db.SaveChangesAsync();
        }

        // Act: FK-safe bulk delete (no seeding)
        await Cleaner.DeleteAllDataAsync();

        // Assert
        (await CountItemsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task reset_all_data_clears_then_applies_seed_data()
    {
        // Arrange: insert extra data that should be removed
        using (var scope = _ctx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            db.Items.Add(new Item { Id = Guid.NewGuid(), Name = "Noise Item" });
            await db.SaveChangesAsync();
        }

        // Act: delete all + run IInitialData<T> seeders
        await Cleaner.ResetAllDataAsync();

        // Assert: exactly the seed rows remain
        using var checkScope = _ctx.Host.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<ItemsDbContext>();
        var items = await checkDb.Items.OrderBy(x => x.Name).ToListAsync();

        items.Count.ShouldBe(SeedItemsForTests.Items.Length);
        items.Select(x => x.Name).ShouldBe(
            SeedItemsForTests.Items.Select(x => x.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task database_cleaner_is_registered_as_singleton()
    {
        var cleaner1 = _ctx.Host.Services.GetRequiredService<IDatabaseCleaner<ItemsDbContext>>();
        var cleaner2 = _ctx.Host.Services.GetRequiredService<IDatabaseCleaner<ItemsDbContext>>();
        cleaner1.ShouldBeSameAs(cleaner2);
    }
}
