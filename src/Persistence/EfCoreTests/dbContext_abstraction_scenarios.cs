using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Tracking;

// Sub-namespace so this file's `OrderEntity` / `IOrderRepository` / etc. don't collide with the
// unrelated identically-named fixtures in `Bug_252_codegen_issue.cs` under the parent namespace.
namespace EfCoreTests.DbContextAbstractionScenarios;

// Follow-up coverage for PR #2919 (`WithDbContextAbstraction<TAbstraction, TDbContext>()`). The
// scenarios in this file prove three contracts that aren't exercised by the original PR's tests:
//
//   1. Multi-DbContext, mixed abstraction — one DbContext is registered through an abstraction
//      and another DbContext is used directly. Both handlers transact through Wolverine's
//      EF Core middleware in the same host.
//
//   2. Multiple abstractions for the SAME DbContext — `StoreDbContext` implements both
//      `IItemRepository` and `IOrderRepository`. Two separate handlers, each depending on a
//      different abstraction, both successfully commit through the same physical DbContext.
//
//   3. Multiple abstractions used IN THE SAME handler — the handler depends on both
//      `IItemRepository` and `IOrderRepository`. At runtime BOTH parameters must resolve to the
//      *same scoped* `StoreDbContext` instance (just cast to the requested interface), so a
//      single Save/transaction commits all writes atomically. This is the contract the
//      DI-registration pattern (forwarding factories) is designed to honour, and the cast frame
//      that the merged PR emits depends on.

public class dbContext_abstraction_scenarios
{
    // --- Scenario 1: multi-DbContext, mixed abstraction ---------------------------------------

    [Fact]
    public async Task multiple_dbcontext_types_one_abstracted_one_direct()
    {
        await CreateSchemaAsync("""
            CREATE TABLE orders_abs_schema.orders (
                "Id" uuid PRIMARY KEY,
                "Description" text NOT NULL
            );
            CREATE TABLE customers_abs_schema.customers (
                "Id" uuid PRIMARY KEY,
                "Name" text NOT NULL
            );
            """, "orders_abs_schema", "customers_abs_schema");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                #region sample_register_mixed_dbcontexts

                // First DbContext: abstracted via IOrderRepository.
                opts.Services.AddDbContextWithWolverineIntegration<OrdersDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString,
                        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "orders_abs_schema")));
                opts.Services.AddScoped<IOrderRepository>(sp => sp.GetRequiredService<OrdersDbContext>());

                // Second DbContext: used directly, no abstraction.
                opts.Services.AddDbContextWithWolverineIntegration<CustomersDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString,
                        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "customers_abs_schema")));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_abs");

                // Only OrdersDbContext is registered as having an abstraction — Wolverine's
                // transactional middleware still wraps handlers that depend on
                // CustomersDbContext directly.
                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<IOrderRepository, OrdersDbContext>();

                #endregion

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PlaceOrderViaAbstractionHandler>()
                    .IncludeType<RegisterCustomerDirectHandler>();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new PlaceOrderViaAbstraction(orderId, "abstracted"));
        await host.InvokeMessageAndWaitAsync(new RegisterCustomerDirect(customerId, "direct"));

        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<OrdersDbContext>()
                .Orders.AnyAsync(o => o.Id == orderId))
            .ShouldBeTrue("abstracted handler must commit through the IOrderRepository transaction");
        (await scope.ServiceProvider.GetRequiredService<CustomersDbContext>()
                .Customers.AnyAsync(c => c.Id == customerId))
            .ShouldBeTrue("direct handler must commit through the CustomersDbContext transaction");
    }

    // --- Scenario 2: multiple abstractions for the same DbContext ------------------------------

    [Fact]
    public async Task multiple_abstractions_for_same_dbcontext_each_used_independently()
    {
        await CreateSchemaAsync("""
            CREATE TABLE store_abs_schema.items (
                "Id" uuid PRIMARY KEY,
                "Name" text NOT NULL
            );
            CREATE TABLE store_abs_schema.orders (
                "Id" uuid PRIMARY KEY,
                "Status" text NOT NULL
            );
            """, "store_abs_schema");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                #region sample_register_multiple_dbcontext_abstractions

                opts.Services.AddDbContextWithWolverineIntegration<StoreDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString,
                        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "store_abs_schema")));

                // Two abstractions forwarded to the SAME scoped DbContext instance via factory
                // lambdas. `AddScoped<TAbs, TImpl>()` would create *separate* instances per
                // registration; the factory form is the one users want when an abstraction is
                // just a view over a DbContext that's already in the scope.
                opts.Services.AddScoped<IItemRepository>(sp => sp.GetRequiredService<StoreDbContext>());
                opts.Services.AddScoped<IOrderInsightRepository>(sp => sp.GetRequiredService<StoreDbContext>());

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_abs");

                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<IItemRepository, StoreDbContext>()
                    .WithDbContextAbstraction<IOrderInsightRepository, StoreDbContext>();

                #endregion

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<CreateStoreItemHandler>()
                    .IncludeType<RecordStoreOrderHandler>();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        var itemId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new CreateStoreItem(itemId, "phone"));
        await host.InvokeMessageAndWaitAsync(new RecordStoreOrder(orderId, "shipped"));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        (await db.Items.AnyAsync(i => i.Id == itemId)).ShouldBeTrue();
        (await db.StoreOrders.AnyAsync(o => o.Id == orderId)).ShouldBeTrue();
    }

    // --- Scenario 3: same handler uses both abstractions; assert SAME DbContext instance -------

    [Fact]
    public async Task handler_using_two_abstractions_to_same_dbcontext_uses_one_scoped_instance()
    {
        await CreateSchemaAsync("""
            CREATE TABLE store_abs_schema.items (
                "Id" uuid PRIMARY KEY,
                "Name" text NOT NULL
            );
            CREATE TABLE store_abs_schema.orders (
                "Id" uuid PRIMARY KEY,
                "Status" text NOT NULL
            );
            """, "store_abs_schema");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<StoreDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString,
                        b => b.MigrationsHistoryTable("__EFMigrationsHistory", "store_abs_schema")));

                // Critical for the contract under test: both abstractions are factory-resolved
                // from the same scoped StoreDbContext. Any other registration shape would defeat
                // the "one DbContext in scope, viewed through different interfaces" guarantee.
                opts.Services.AddScoped<IItemRepository>(sp => sp.GetRequiredService<StoreDbContext>());
                opts.Services.AddScoped<IOrderInsightRepository>(sp => sp.GetRequiredService<StoreDbContext>());

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_abs");

                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<IItemRepository, StoreDbContext>()
                    .WithDbContextAbstraction<IOrderInsightRepository, StoreDbContext>();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<CrossAbstractionAuditHandler>();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        var itemId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // Reset between scenarios so we observe only this command's writes.
        CrossAbstractionAuditHandler.LastSeen = default;

        await host.InvokeMessageAndWaitAsync(new CrossAbstractionAudit(itemId, orderId));

        // The handler tested reference-equality between the two abstraction parameters cast back
        // to the concrete DbContext; this assertion is the test's actual proof point.
        CrossAbstractionAuditHandler.LastSeen.SameInstance
            .ShouldBeTrue("both abstractions must resolve to the same scoped StoreDbContext");

        // And both writes must have landed via that single context's single SaveChanges.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        (await db.Items.AnyAsync(i => i.Id == itemId)).ShouldBeTrue();
        (await db.StoreOrders.AnyAsync(o => o.Id == orderId)).ShouldBeTrue();
    }

    // EF Core's EnsureCreatedAsync is a no-op when the database already exists — and the shared
    // Wolverine integration-tests Postgres always does. So we issue raw DDL for the user-defined
    // tables, mirroring the workaround documented in `Bugs/Bug_DurableLocalQueue_ancillary_store_routing.cs`
    // (see GH-2618). Schemas are dropped first so re-runs of these scenarios start clean.
    private static async Task CreateSchemaAsync(string sql, params string[] schemas)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        foreach (var schema in schemas)
        {
            await conn.DropSchemaAsync(schema);
            await conn.CreateCommand($"CREATE SCHEMA \"{schema}\"").ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

// === Entities + DbContexts + abstractions ====================================================

public class OrderEntity
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class CustomerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class StoreItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class StoreOrder
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
}

public interface IOrderRepository
{
    DbSet<OrderEntity> Orders { get; }
}

public interface IItemRepository
{
    DbSet<StoreItem> Items { get; }
}

public interface IOrderInsightRepository
{
    DbSet<StoreOrder> StoreOrders { get; }
}

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options), IOrderRepository
{
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orders_abs_schema");
        modelBuilder.MapWolverineEnvelopeStorage("orders_abs_schema");
        modelBuilder.Entity<OrderEntity>().ToTable("orders");
    }
}

public class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("customers_abs_schema");
        modelBuilder.MapWolverineEnvelopeStorage("customers_abs_schema");
        modelBuilder.Entity<CustomerEntity>().ToTable("customers");
    }
}

public class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options),
    IItemRepository, IOrderInsightRepository
{
    public DbSet<StoreItem> Items => Set<StoreItem>();
    public DbSet<StoreOrder> StoreOrders => Set<StoreOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("store_abs_schema");
        modelBuilder.MapWolverineEnvelopeStorage("store_abs_schema");
        modelBuilder.Entity<StoreItem>().ToTable("items");
        modelBuilder.Entity<StoreOrder>().ToTable("orders");
    }
}

// === Messages ================================================================================

public record PlaceOrderViaAbstraction(Guid Id, string Description);

public record RegisterCustomerDirect(Guid Id, string Name);

public record CreateStoreItem(Guid Id, string Name);

public record RecordStoreOrder(Guid Id, string Status);

public record CrossAbstractionAudit(Guid ItemId, Guid OrderId);

// === Handlers ================================================================================

#region sample_handler_using_dbcontext_abstraction

public class PlaceOrderViaAbstractionHandler
{
    public static void Handle(PlaceOrderViaAbstraction cmd, IOrderRepository orders)
    {
        // The handler depends on the abstraction. Wolverine's transactional middleware
        // recognises the chain as `DbContext`-backed via the registered abstraction and emits
        // a runtime cast at the top of the chain so SaveChangesAsync + outbox enrolment fire
        // against the concrete OrdersDbContext underneath.
        orders.Orders.Add(new OrderEntity { Id = cmd.Id, Description = cmd.Description });
    }
}

#endregion

public class RegisterCustomerDirectHandler
{
    public static void Handle(RegisterCustomerDirect cmd, CustomersDbContext db)
    {
        db.Customers.Add(new CustomerEntity { Id = cmd.Id, Name = cmd.Name });
    }
}

public class CreateStoreItemHandler
{
    public static void Handle(CreateStoreItem cmd, IItemRepository items)
    {
        items.Items.Add(new StoreItem { Id = cmd.Id, Name = cmd.Name });
    }
}

public class RecordStoreOrderHandler
{
    public static void Handle(RecordStoreOrder cmd, IOrderInsightRepository orders)
    {
        orders.StoreOrders.Add(new StoreOrder { Id = cmd.Id, Status = cmd.Status });
    }
}

#region sample_handler_using_multiple_abstractions

public class CrossAbstractionAuditHandler
{
    public static (bool SameInstance, Type ItemsType, Type OrdersType) LastSeen;

    // The handler depends on TWO abstractions of the same `DbContext`. At runtime both
    // parameters resolve to the same scoped `StoreDbContext`, just viewed through different
    // interfaces — so a single `SaveChangesAsync` commits writes the handler made through
    // either parameter atomically. The forwarding-factory DI registrations above are what
    // make this work; without them you'd get two separate `DbContext` instances.
    public static void Handle(CrossAbstractionAudit cmd, IItemRepository items, IOrderInsightRepository orders)
    {
        // Cast both back to the concrete DbContext - the cast must succeed (the constraint on
        // WithDbContextAbstraction guarantees TDbContext : TAbstraction) and the resulting
        // references must be the SAME instance. That's the contract Wolverine's
        // CastDbContextFrame + the user's forwarding-factory DI registrations together provide:
        // one DbContext in scope, viewed through different interfaces.
        var itemsCtx = (StoreDbContext)items;
        var ordersCtx = (StoreDbContext)orders;

        LastSeen = (ReferenceEquals(itemsCtx, ordersCtx), itemsCtx.GetType(), ordersCtx.GetType());

        // Both writes go through the single scoped DbContext - the EF Core middleware's
        // SaveChangesAsync postprocessor commits them as one transaction.
        items.Items.Add(new StoreItem { Id = cmd.ItemId, Name = "cross-abs" });
        orders.StoreOrders.Add(new StoreOrder { Id = cmd.OrderId, Status = "audited" });
    }
}

#endregion

// === Doc-only sample: bare single-abstraction registration ====================================
//
// Existence-of-this-class proves the snippet below compiles; the dedicated tests above exercise
// the actual runtime behaviour.

public static class SingleAbstractionRegistrationSample
{
    public static void Configure(WolverineOptions opts, string connectionString)
    {
        #region sample_register_dbcontext_abstraction

        opts.Services.AddDbContextWithWolverineIntegration<OrdersDbContext>(x =>
            x.UseNpgsql(connectionString));

        // Forward the abstraction to the SAME scoped DbContext via a factory. This keeps
        // `IOrderRepository` and `OrdersDbContext` pointing at one instance per scope, which is
        // what `AddScoped<TAbs, TImpl>()` does NOT do (it would create a separate one per
        // registered interface).
        opts.Services.AddScoped<IOrderRepository>(sp => sp.GetRequiredService<OrdersDbContext>());

        opts.PersistMessagesWithPostgresql(connectionString, "wolverine");

        opts.UseEntityFrameworkCoreTransactions()
            .WithDbContextAbstraction<IOrderRepository, OrdersDbContext>();

        opts.Policies.AutoApplyTransactions();

        #endregion
    }
}
