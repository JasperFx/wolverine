using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.QueryPlans;

/// <summary>
/// weasel#621. Two batch-capable plans on one DbContext are batched into a single Weasel
/// BatchedQuery, and that used to change what the plans returned: owned types, JSON members and
/// Include'd navigations all came back empty, and a collection Include fanned one parent out into
/// one parent per child row. Nothing reported an error.
/// </summary>
/// <remarks>
/// The entity here is deliberately NOT flat -- an owned type, an owned type mapped to a JSON
/// column, and a collection navigation -- because a flat entity cannot see this defect at all.
/// The tables are created with raw SQL rather than migrations so the shape under test is exactly
/// what is asserted.
/// </remarks>
[Collection("sqlserver")]
public class weasel_621_batched_plan_fidelity : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();

            foreach (var sql in new[]
                     {
                         "IF OBJECT_ID('w621.order_lines','U') IS NOT NULL DROP TABLE w621.order_lines;",
                         "IF OBJECT_ID('w621.orders','U') IS NOT NULL DROP TABLE w621.orders;",
                         "IF SCHEMA_ID('w621') IS NULL EXEC('CREATE SCHEMA w621');",
                         """
                         CREATE TABLE w621.orders (
                             Id uniqueidentifier NOT NULL PRIMARY KEY,
                             Customer nvarchar(100) NOT NULL,
                             ShippingAddress_City nvarchar(100) NULL,
                             Settings nvarchar(max) NULL);
                         """,
                         """
                         CREATE TABLE w621.order_lines (
                             Id uniqueidentifier NOT NULL PRIMARY KEY,
                             OrderId uniqueidentifier NOT NULL,
                             Sku nvarchar(50) NOT NULL);
                         """
                     })
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<RichOrdersDbContext>(
                    o => o.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "w621_wolverine");
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<LoadOrderAndSiblingsHandler>();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Guid> seedAsync()
    {
        var id = Guid.NewGuid();

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RichOrdersDbContext>();

        db.Orders.Add(new RichOrder
        {
            Id = id,
            Customer = "acme",
            ShippingAddress = new RichAddress { City = "Oslo" },
            Settings = new RichSettings { Gift = true },
            Lines =
            [
                new RichOrderLine { Id = Guid.NewGuid(), Sku = "A" },
                new RichOrderLine { Id = Guid.NewGuid(), Sku = "B" }
            ]
        });

        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task batched_plans_return_what_ef_core_returns()
    {
        var id = await seedAsync();

        // The control: what the same query returns outside a batch
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RichOrdersDbContext>();
            var direct = await db.Orders.Include(x => x.Lines)
                .SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);

            direct.ShippingAddress.ShouldNotBeNull().City.ShouldBe("Oslo");
            direct.Settings.ShouldNotBeNull().Gift.ShouldBeTrue();
            direct.Lines.Count.ShouldBe(2);
        }

        LoadOrderAndSiblingsHandler.Loaded = null;
        LoadOrderAndSiblingsHandler.LoadedList = null;

        // Two batch-capable plans in one handler, so EFCoreBatchingPolicy batches them
        await _host.InvokeMessageAndWaitAsync(new FindOrderAndSiblings(id, "acme"));

        var order = LoadOrderAndSiblingsHandler.Loaded.ShouldNotBeNull();

        order.ShippingAddress.ShouldNotBeNull().City.ShouldBe("Oslo");
        order.Settings.ShouldNotBeNull().Gift.ShouldBeTrue();
        order.Lines.Count.ShouldBe(2);

        // A collection Include must not turn one parent into one parent per child row
        LoadOrderAndSiblingsHandler.LoadedList.ShouldNotBeNull().Count.ShouldBe(1);
    }
}

public record FindOrderAndSiblings(Guid Id, string Customer);

public class LoadOrderAndSiblingsHandler
{
    public static RichOrder? Loaded;
    public static IReadOnlyList<RichOrder>? LoadedList;

    public static (RichOrderById, RichOrdersForCustomer) Load(FindOrderAndSiblings msg)
        => (new RichOrderById(msg.Id), new RichOrdersForCustomer(msg.Customer));

    public static void Handle(FindOrderAndSiblings msg, RichOrder? order, IReadOnlyList<RichOrder> siblings)
    {
        Loaded = order;
        LoadedList = siblings;
    }
}

public class RichOrderById(Guid id) : QueryPlan<RichOrdersDbContext, RichOrder>
{
    public override IQueryable<RichOrder> Query(RichOrdersDbContext db)
        => db.Orders.Include(x => x.Lines).Where(x => x.Id == id);
}

public class RichOrdersForCustomer(string customer) : QueryListPlan<RichOrdersDbContext, RichOrder>
{
    public override IQueryable<RichOrder> Query(RichOrdersDbContext db)
        => db.Orders.Include(x => x.Lines).Where(x => x.Customer == customer);
}

public class RichOrder
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = "";
    public RichAddress ShippingAddress { get; set; } = null!;
    public RichSettings Settings { get; set; } = null!;
    public List<RichOrderLine> Lines { get; set; } = [];
}

public class RichAddress
{
    public string City { get; set; } = "";
}

public class RichSettings
{
    public bool Gift { get; set; }
}

public class RichOrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Sku { get; set; } = "";
}

public class RichOrdersDbContext(DbContextOptions<RichOrdersDbContext> options) : DbContext(options)
{
    public DbSet<RichOrder> Orders => Set<RichOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RichOrder>(map =>
        {
            map.ToTable("orders", "w621");
            map.HasKey(x => x.Id);
            map.Property(x => x.Customer);

            map.OwnsOne(x => x.ShippingAddress);
            map.OwnsOne(x => x.Settings, s => s.ToJson());

            map.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId);
        });

        modelBuilder.Entity<RichOrderLine>(map =>
        {
            map.ToTable("order_lines", "w621");
            map.HasKey(x => x.Id);
        });
    }
}
