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
/// Verifies the GH-2539 dev-time ergonomics wins on the Wolverine side:
///   1. <see cref="WolverineEntityCoreExtensions.UseEntityFrameworkCoreTransactions(WolverineOptions)"/>
///      auto-registers Weasel's open-generic <see cref="DatabaseCleaner{TContext}"/> /
///      <see cref="IDatabaseCleaner{TContext}"/> so callers no longer need a per-context
///      <c>services.AddDatabaseCleaner&lt;T&gt;()</c>.
///   2. The new <c>host.ResetAllDataAsync&lt;T&gt;()</c> extension reaches through a
///      scope, resolves the cleaner, and wipes+reseeds the one DbContext.
/// </summary>
public class AutoDatabaseCleanerContext : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddDbContext<ItemsDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));

                // NOTE: no explicit AddDatabaseCleaner<ItemsDbContext>() here — that
                // is exactly the registration we're eliminating. Seed data still
                // needs to be registered; only the cleaner itself is now automatic.
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

[Collection("sqlserver")]
public class auto_database_cleaner_registration_tests : IClassFixture<AutoDatabaseCleanerContext>
{
    private readonly AutoDatabaseCleanerContext _ctx;

    public auto_database_cleaner_registration_tests(AutoDatabaseCleanerContext ctx)
    {
        _ctx = ctx;
    }

    [Fact]
    public void IDatabaseCleaner_of_T_resolves_without_explicit_registration()
    {
        // UseEntityFrameworkCoreTransactions() alone should be enough.
        var cleaner = _ctx.Host.Services.GetRequiredService<IDatabaseCleaner<ItemsDbContext>>();
        cleaner.ShouldNotBeNull();
        cleaner.ShouldBeOfType<DatabaseCleaner<ItemsDbContext>>();
    }

    [Fact]
    public void concrete_DatabaseCleaner_of_T_also_resolves()
    {
        // Consumers that want the concrete type (e.g. internal helpers) should
        // also get it from DI without registering it themselves.
        var cleaner = _ctx.Host.Services.GetRequiredService<DatabaseCleaner<ItemsDbContext>>();
        cleaner.ShouldNotBeNull();
    }

    [Fact]
    public void auto_registration_is_singleton()
    {
        var a = _ctx.Host.Services.GetRequiredService<IDatabaseCleaner<ItemsDbContext>>();
        var b = _ctx.Host.Services.GetRequiredService<IDatabaseCleaner<ItemsDbContext>>();
        a.ShouldBeSameAs(b);
    }

    [Fact]
    public async Task host_ResetAllDataAsync_deletes_then_reseeds()
    {
        // Seed noise that should not survive the reset.
        using (var scope = _ctx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            db.Items.Add(new Item { Id = Guid.NewGuid(), Name = "Noise" });
            await db.SaveChangesAsync();
        }

        // Act: the new one-liner for test teardown.
        await _ctx.Host.ResetAllDataAsync<ItemsDbContext>();

        using var check = _ctx.Host.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<ItemsDbContext>();
        var items = await checkDb.Items.OrderBy(x => x.Name).ToListAsync();

        items.Select(x => x.Name).ShouldBe(
            SeedItemsForTests.Items.Select(x => x.Name).OrderBy(n => n));
    }
}
