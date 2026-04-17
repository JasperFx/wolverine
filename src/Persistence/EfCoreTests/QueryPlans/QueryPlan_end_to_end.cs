using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.QueryPlans;

/// <summary>
/// End-to-end smoke test proving a handler can consume an IQueryPlan against
/// the real EF Core + Wolverine transactional pipeline — not just the
/// in-memory provider. GH-2505.
/// </summary>
[Collection("sqlserver")]
public class QueryPlan_end_to_end : IAsyncLifetime
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
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "qpe2e");
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task handler_uses_query_plan_to_approve_matching_items()
    {
        var prefix = $"qp_{Guid.NewGuid():N}"[..16];

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            db.Items.Add(new Item { Id = Guid.NewGuid(), Name = $"{prefix}_a", Approved = false });
            db.Items.Add(new Item { Id = Guid.NewGuid(), Name = $"{prefix}_b", Approved = false });
            db.Items.Add(new Item { Id = Guid.NewGuid(), Name = "untouched",   Approved = false });
            await db.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new ApproveItemsByPrefix(prefix));

        using var verify = _host.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ItemsDbContext>();

        var approved = await verifyDb.Items
            .Where(x => x.Name.StartsWith(prefix) && x.Approved)
            .CountAsync();
        approved.ShouldBe(2);

        var untouched = await verifyDb.Items.SingleAsync(x => x.Name == "untouched");
        untouched.Approved.ShouldBeFalse();
    }
}

public record ApproveItemsByPrefix(string Prefix);

public static class ApproveItemsByPrefixHandler
{
    public static async Task Handle(ApproveItemsByPrefix msg, ItemsDbContext db, CancellationToken ct)
    {
        // The reusable query lives in its own testable class. The handler
        // just consumes it — no repository, no ad-hoc LINQ embedded in the
        // handler body.
        var items = await db.QueryByPlanAsync(new ItemsWithNamePrefix(msg.Prefix), ct);

        foreach (var item in items)
        {
            item.Approved = true;
        }
    }
}

public class ItemsWithNamePrefix(string prefix) : QueryListPlan<ItemsDbContext, Item>
{
    public override IQueryable<Item> Query(ItemsDbContext db)
        => db.Items.Where(x => x.Name.StartsWith(prefix));
}
