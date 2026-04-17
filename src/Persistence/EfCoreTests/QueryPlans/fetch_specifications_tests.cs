using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.QueryPlans;

/// <summary>
/// GH-2527: Handlers may return Wolverine.EntityFrameworkCore IQueryPlan
/// (or IBatchQueryPlan) instances directly from Load methods — singly or in a
/// tuple — and Wolverine auto-executes them, batching multiple batch-capable
/// plans against the same DbContext into one Weasel BatchedQuery round-trip.
/// The [FromQuerySpecification] attribute provides the same ergonomics on
/// handler parameters without writing a Load method.
/// </summary>
[Collection("sqlserver")]
public class efcore_fetch_specifications_tests : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<ItemsDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
            })
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "ef_fetchspecs");
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<LoadItemByNameHandler>()
                    .IncludeType<LoadMatchingItemsHandler>()
                    .IncludeType<LoadItemAndListHandler>()
                    .IncludeType<LoadItemViaAttributeHandler>();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task SeedAsync(params Item[] items)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
        foreach (var item in items) db.Items.Add(item);
        await db.SaveChangesAsync();
    }

    // ─────────────────── single plan via Load ───────────────────

    [Fact]
    public async Task load_returning_single_query_plan_runs_and_relays_to_handler()
    {
        var uniq = $"single_{Guid.NewGuid():N}"[..16];
        await SeedAsync(new Item { Id = Guid.NewGuid(), Name = uniq });

        LoadItemByNameHandler.LastSeenId = Guid.Empty;
        await _host.InvokeMessageAndWaitAsync(new FindItemByName(uniq));

        LoadItemByNameHandler.LastSeenId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task load_returning_list_plan_runs_and_relays_to_handler()
    {
        var prefix = $"list_{Guid.NewGuid():N}"[..12];
        await SeedAsync(
            new Item { Id = Guid.NewGuid(), Name = $"{prefix}_a" },
            new Item { Id = Guid.NewGuid(), Name = $"{prefix}_b" },
            new Item { Id = Guid.NewGuid(), Name = $"{prefix}_c" });

        LoadMatchingItemsHandler.LastCount = -1;
        await _host.InvokeMessageAndWaitAsync(new FindItemsByPrefix(prefix));

        LoadMatchingItemsHandler.LastCount.ShouldBe(3);
    }

    // ───────────── tuple return: both batched ─────────────

    [Fact]
    public async Task load_returning_tuple_of_plans_runs_both_and_batches()
    {
        var prefix = $"tup_{Guid.NewGuid():N}"[..12];
        var targetName = $"{prefix}_target";
        await SeedAsync(
            new Item { Id = Guid.NewGuid(), Name = targetName },
            new Item { Id = Guid.NewGuid(), Name = $"{prefix}_other1" },
            new Item { Id = Guid.NewGuid(), Name = $"{prefix}_other2" });

        LoadItemAndListHandler.LastSeenName = null;
        LoadItemAndListHandler.LastCount = -1;

        await _host.InvokeMessageAndWaitAsync(new FindItemAndSiblings(targetName, prefix));

        LoadItemAndListHandler.LastSeenName.ShouldBe(targetName);
        LoadItemAndListHandler.LastCount.ShouldBe(3);
    }

    // ─────────────── [FromQuerySpecification] ───────────────

    [Fact]
    public async Task from_query_specification_attribute_constructs_spec_from_message_fields()
    {
        var uniq = $"attr_{Guid.NewGuid():N}"[..16];
        await SeedAsync(new Item { Id = Guid.NewGuid(), Name = uniq });

        LoadItemViaAttributeHandler.LastSeenId = Guid.Empty;
        await _host.InvokeMessageAndWaitAsync(new FindItemByNameViaAttribute(uniq));

        LoadItemViaAttributeHandler.LastSeenId.ShouldNotBe(Guid.Empty);
    }
}

// ─────────────────── Messages ───────────────────

public record FindItemByName(string Name);
public record FindItemsByPrefix(string Prefix);
public record FindItemAndSiblings(string Name, string Prefix);
public record FindItemByNameViaAttribute(string Name);

// ─────────────────── Plans ───────────────────

/// <summary>Single-item plan using the <see cref="QueryPlan{TDb,TEntity}"/>
/// convenience base — implements both IQueryPlan AND IBatchQueryPlan.</summary>
public class ItemByNamePlan(string name) : QueryPlan<ItemsDbContext, Item>
{
    public string Name { get; } = name;

    public override IQueryable<Item> Query(ItemsDbContext db)
        => db.Items.Where(x => x.Name == Name);
}

/// <summary>List plan using the <see cref="QueryListPlan{TDb,TEntity}"/>
/// convenience base — implements both IQueryPlan AND IBatchQueryPlan.</summary>
public class ItemsByPrefixPlan(string prefix) : QueryListPlan<ItemsDbContext, Item>
{
    public string Prefix { get; } = prefix;

    public override IQueryable<Item> Query(ItemsDbContext db)
        => db.Items.Where(x => x.Name.StartsWith(Prefix));
}

// ─────────────────── Handlers ───────────────────

public class LoadItemByNameHandler
{
    public static Guid LastSeenId = Guid.Empty;

    public static ItemByNamePlan Load(FindItemByName msg) => new(msg.Name);

    public static void Handle(FindItemByName msg, Item? item)
    {
        if (item is not null) LastSeenId = item.Id;
    }
}

public class LoadMatchingItemsHandler
{
    public static int LastCount = -1;

    public static ItemsByPrefixPlan Load(FindItemsByPrefix msg) => new(msg.Prefix);

    public static void Handle(FindItemsByPrefix msg, IReadOnlyList<Item> items)
    {
        LastCount = items.Count;
    }
}

public class LoadItemAndListHandler
{
    public static string? LastSeenName;
    public static int LastCount = -1;

    // Tuple return — both plans are batch-capable and share one DbContext,
    // so Wolverine batches them into a single BatchedQuery round-trip.
    public static (ItemByNamePlan, ItemsByPrefixPlan) Load(FindItemAndSiblings msg)
        => (new(msg.Name), new(msg.Prefix));

    public static void Handle(FindItemAndSiblings msg, Item? item, IReadOnlyList<Item> siblings)
    {
        LastSeenName = item?.Name;
        LastCount = siblings.Count;
    }
}

public class LoadItemViaAttributeHandler
{
    public static Guid LastSeenId = Guid.Empty;

    // No Load method — the attribute's codegen constructs the spec from message
    // fields (Name ctor param ↔ msg.Name member) and executes it.
    public static void Handle(
        FindItemByNameViaAttribute msg,
        [FromQuerySpecification(typeof(ItemByNamePlan))] Item? item)
    {
        if (item is not null) LastSeenId = item.Id;
    }
}
