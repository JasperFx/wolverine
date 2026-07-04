using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Postgresql;
using Wolverine.Tracking;

// Sub-namespace so this file's fixtures don't collide with the identically-shaped ones in
// transactional_dbcontext_selection_tests.cs (which cover the [Transactional]-attribute path).
namespace EfCoreTests.RegistrationSelection;

// Companion to transactional_dbcontext_selection_tests.cs. That file proves the *handler-side*
// disambiguation: [Transactional(typeof(TDbContext))]. This file proves the *registration-side*
// story required by Clean Architecture / modular monoliths, where the application-layer handler
// must carry no DbContext reference AND no [Transactional] attribute at all:
//
//   * Layer A (this test): when a chain has more than one DbContext-shaped dependency but only ONE
//     of them is Wolverine-enabled (mapped Wolverine envelope storage / registered via
//     AddDbContextWithWolverineIntegration), Wolverine auto-selects that one. A plain read-only
//     lookup DbContext registered with vanilla AddDbContext<> can never own the outbox, so it is
//     not a transactional candidate. Zero configuration, zero handler references.
public class transactional_dbcontext_registration_selection_tests
{
    [Fact]
    public async Task auto_selects_the_only_wolverine_enabled_dbcontext_when_the_other_is_a_plain_read_context()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("ledger_reg_schema");
            await conn.CreateCommand(
                    """
                    CREATE SCHEMA "ledger_reg_schema";
                    CREATE TABLE ledger_reg_schema.entries (
                        "Id" uuid PRIMARY KEY,
                        "Memo" text NOT NULL
                    );
                    """)
                .ExecuteNonQueryAsync();
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // The write side: enrolled in the transaction + outbox.
                opts.Services.AddDbContextWithWolverineIntegration<LedgerDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                // The read side: a plain AddDbContext<> lookup context with NO Wolverine envelope
                // storage of its own. It is DbContext-shaped (so Wolverine sees it as a candidate)
                // but can never be the transactional/outbox owner.
                opts.Services.AddDbContext<RatesDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_ledger_reg");

                opts.UseEntityFrameworkCoreTransactions();

                // Note: NO [Transactional] attribute on the handler, NO explicit selection here.
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PostLedgerEntryHandler>();
            }).StartAsync();

        // Force lazy chain compilation the same way transaction_middleware_mode_tests.cs does.
        host.GetRuntime().Handlers.HandlerFor<PostLedgerEntry>();
        var chain = host.GetRuntime().Handlers.ChainFor<PostLedgerEntry>();
        chain.ShouldNotBeNull();

        // Only the Wolverine-enabled LedgerDbContext should be enrolled.
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ToArray().Length.ShouldBe(1);

        var saveChanges = chain.Postprocessors.OfType<MethodCall>()
            .Single(x => x.Method.Name == nameof(DbContext.SaveChangesAsync));
        saveChanges.HandlerType.ShouldBe(typeof(LedgerDbContext));

        var entryId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new PostLedgerEntry(entryId, "opening balance"));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        (await db.Entries.AnyAsync(e => e.Id == entryId)).ShouldBeTrue();
    }

    // --- Layer B: explicit registration-side selection --------------------------------------------
    // When BOTH DbContexts are Wolverine-enabled (e.g. two modules that each own an outbox), the
    // automatic rule can't disambiguate. The composition root selects the write context with
    // WithTransactionalDbContext<T>() - the handler still carries no DbContext reference.

    [Fact]
    public async Task explicit_registration_selection_by_concrete_type_disambiguates_two_enabled_contexts()
    {
        await createSalesAndAuditSchemasAsync();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<SalesDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));
                opts.Services.AddDbContextWithWolverineIntegration<AuditDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_sales_reg");

                opts.UseEntityFrameworkCoreTransactions()
                    .WithTransactionalDbContext<SalesDbContext>();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<RecordSaleHandler>();
            }).StartAsync();

        host.GetRuntime().Handlers.HandlerFor<RecordSale>();
        var chain = host.GetRuntime().Handlers.ChainFor<RecordSale>();
        chain.ShouldNotBeNull();

        chain.Middleware.OfType<EnrollDbContextInTransaction>().ToArray().Length.ShouldBe(1);
        chain.Postprocessors.OfType<MethodCall>()
            .Single(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .HandlerType.ShouldBe(typeof(SalesDbContext));

        var saleId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new RecordSale(saleId, "widget"));

        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SalesDbContext>()
            .Sales.AnyAsync(s => s.Id == saleId)).ShouldBeTrue();
    }

    [Fact]
    public async Task explicit_registration_selection_can_be_a_registered_abstraction()
    {
        await createSalesAndAuditSchemasAsync();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<SalesDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));
                opts.Services.AddScoped<ISalesStore>(sp => sp.GetRequiredService<SalesDbContext>());
                opts.Services.AddDbContextWithWolverineIntegration<AuditDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_sales_reg");

                // The application layer only knows ISalesStore; the concrete DbContext never leaks.
                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<ISalesStore, SalesDbContext>()
                    .WithTransactionalDbContext<ISalesStore>();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<RecordSaleViaStoreHandler>();
            }).StartAsync();

        host.GetRuntime().Handlers.HandlerFor<RecordSaleViaStore>();
        var chain = host.GetRuntime().Handlers.ChainFor<RecordSaleViaStore>();
        chain.ShouldNotBeNull();

        chain.Postprocessors.OfType<MethodCall>()
            .Single(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .HandlerType.ShouldBe(typeof(SalesDbContext));

        var saleId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new RecordSaleViaStore(saleId, "gizmo"));

        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SalesDbContext>()
            .Sales.AnyAsync(s => s.Id == saleId)).ShouldBeTrue();
    }

    [Fact]
    public async Task registration_selection_scoped_per_handler_supports_modular_monolith()
    {
        await createSalesAndAuditSchemasAsync();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<SalesDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));
                opts.Services.AddDbContextWithWolverineIntegration<AuditDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_sales_reg");

                // Each "module" declares its own write context for its own handlers. Both handlers
                // depend on both contexts, but each writes only through its module's context.
                opts.UseEntityFrameworkCoreTransactions()
                    .WithTransactionalDbContext<SalesDbContext>(
                        chain => chain.HandlerCalls().Any(c => c.HandlerType == typeof(SalesModuleHandler)))
                    .WithTransactionalDbContext<AuditDbContext>(
                        chain => chain.HandlerCalls().Any(c => c.HandlerType == typeof(AuditModuleHandler)));

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<SalesModuleHandler>()
                    .IncludeType<AuditModuleHandler>();
            }).StartAsync();

        host.GetRuntime().Handlers.HandlerFor<SalesModuleOp>();
        host.GetRuntime().Handlers.HandlerFor<AuditModuleOp>();

        var salesChain = host.GetRuntime().Handlers.ChainFor<SalesModuleOp>();
        salesChain.ShouldNotBeNull();
        salesChain.Postprocessors.OfType<MethodCall>()
            .Single(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .HandlerType.ShouldBe(typeof(SalesDbContext));

        var auditChain = host.GetRuntime().Handlers.ChainFor<AuditModuleOp>();
        auditChain.ShouldNotBeNull();
        auditChain.Postprocessors.OfType<MethodCall>()
            .Single(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .HandlerType.ShouldBe(typeof(AuditDbContext));

        var salesId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new SalesModuleOp(salesId));
        await host.InvokeMessageAndWaitAsync(new AuditModuleOp(auditId));

        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SalesDbContext>()
            .Sales.AnyAsync(s => s.Id == salesId)).ShouldBeTrue();
        (await scope.ServiceProvider.GetRequiredService<AuditDbContext>()
            .AuditRecords.AnyAsync(a => a.Id == auditId)).ShouldBeTrue();
    }

    [Fact]
    public async Task registration_selection_that_is_not_a_dependency_throws_clear_error()
    {
        await createSalesAndAuditSchemasAsync();

        async Task startBadHost()
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;

                    opts.Services.AddDbContextWithWolverineIntegration<SalesDbContext>(x =>
                        x.UseNpgsql(Servers.PostgresConnectionString));
                    opts.Services.AddDbContext<RatesDbContext>(x =>
                        x.UseNpgsql(Servers.PostgresConnectionString));

                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_sales_reg");

                    // Selects AuditDbContext, which this handler does not depend on at all.
                    opts.UseEntityFrameworkCoreTransactions()
                        .WithTransactionalDbContext<AuditDbContext>(
                            chain => chain.HandlerCalls().Any(c => c.HandlerType == typeof(SalesOnlyHandler)));

                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType<SalesOnlyHandler>();
                }).StartAsync();

            host.GetRuntime().Handlers.HandlerFor<SalesOnlyOp>();
        }

        var ex = await Should.ThrowAsync<InvalidOperationException>(startBadHost);
        ex.Message.ShouldContain("is not one of this chain's dependencies");
    }

    [Fact]
    public async Task handler_transactional_attribute_overrides_registration_selection()
    {
        await createSalesAndAuditSchemasAsync();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<SalesDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));
                opts.Services.AddDbContextWithWolverineIntegration<AuditDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_sales_reg");

                // Registration selects Sales globally, but the handler's [Transactional(typeof(Audit))]
                // must win - the per-handler attribute is the most specific signal.
                opts.UseEntityFrameworkCoreTransactions()
                    .WithTransactionalDbContext<SalesDbContext>();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<AttributeWinsHandler>();
            }).StartAsync();

        host.GetRuntime().Handlers.HandlerFor<PrecedenceOp>();
        var chain = host.GetRuntime().Handlers.ChainFor<PrecedenceOp>();
        chain.ShouldNotBeNull();
        chain.Postprocessors.OfType<MethodCall>()
            .Single(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .HandlerType.ShouldBe(typeof(AuditDbContext));
    }

    [Fact]
    public async Task registration_selection_naming_a_non_dependency_on_a_single_context_handler_throws()
    {
        await createSalesAndAuditSchemasAsync();

        async Task startBadHost()
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;

                    opts.Services.AddDbContextWithWolverineIntegration<SalesDbContext>(x =>
                        x.UseNpgsql(Servers.PostgresConnectionString));

                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_sales_reg");

                    // The handler depends only on SalesDbContext, yet this selection (scoped to it)
                    // names AuditDbContext. A mis-scoped selection must fail even when the chain is
                    // otherwise unambiguous, exactly like a mismatched [Transactional(typeof(...))].
                    opts.UseEntityFrameworkCoreTransactions()
                        .WithTransactionalDbContext<AuditDbContext>(
                            chain => chain.HandlerCalls().Any(c => c.HandlerType == typeof(SingleContextHandler)));

                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType<SingleContextHandler>();
                }).StartAsync();

            host.GetRuntime().Handlers.HandlerFor<SingleContextOp>();
        }

        var ex = await Should.ThrowAsync<InvalidOperationException>(startBadHost);
        ex.Message.ShouldContain("is not one of this chain's dependencies");
    }

    private static async Task createSalesAndAuditSchemasAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("sales_reg_schema");
        await conn.DropSchemaAsync("audit_reg_schema");
        await conn.CreateCommand(
                """
                CREATE SCHEMA "sales_reg_schema";
                CREATE TABLE sales_reg_schema.sales (
                    "Id" uuid PRIMARY KEY,
                    "Product" text NOT NULL
                );
                CREATE SCHEMA "audit_reg_schema";
                CREATE TABLE audit_reg_schema.audits (
                    "Id" uuid PRIMARY KEY,
                    "Note" text NOT NULL
                );
                """)
            .ExecuteNonQueryAsync();
    }
}

// === Entities + DbContexts ====================================================================

public class LedgerEntry
{
    public Guid Id { get; set; }
    public string Memo { get; set; } = string.Empty;
}

public class Rate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

// Write side — Wolverine-enabled (maps envelope storage).
public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ledger_reg_schema");
        modelBuilder.MapWolverineEnvelopeStorage("ledger_reg_schema");
        modelBuilder.Entity<LedgerEntry>().ToTable("entries");
    }
}

// Read side — intentionally NOT Wolverine-enabled: no MapWolverineEnvelopeStorage call.
public class RatesDbContext(DbContextOptions<RatesDbContext> options) : DbContext(options)
{
    public DbSet<Rate> Rates => Set<Rate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("rates_reg_schema");
        modelBuilder.Entity<Rate>().ToTable("rates");
    }
}

// === Messages + handlers ======================================================================

public record PostLedgerEntry(Guid Id, string Memo);

public class PostLedgerEntryHandler
{
    // No [Transactional] attribute, no DbContext-type reference anywhere in application code.
    public static void Handle(PostLedgerEntry cmd, LedgerDbContext ledger, RatesDbContext rates)
    {
        ledger.Entries.Add(new LedgerEntry { Id = cmd.Id, Memo = cmd.Memo });
    }
}

// === Layer B fixtures: two Wolverine-enabled contexts + an abstraction ========================

public class Sale
{
    public Guid Id { get; set; }
    public string Product { get; set; } = string.Empty;
}

public class AuditRecord
{
    public Guid Id { get; set; }
    public string Note { get; set; } = string.Empty;
}

// Application-layer abstraction over the sales write store - the only thing a Clean Architecture
// handler is allowed to depend on.
public interface ISalesStore
{
    DbSet<Sale> Sales { get; }
}

public class SalesDbContext(DbContextOptions<SalesDbContext> options) : DbContext(options), ISalesStore
{
    public DbSet<Sale> Sales => Set<Sale>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("sales_reg_schema");
        modelBuilder.MapWolverineEnvelopeStorage("sales_reg_schema");
        modelBuilder.Entity<Sale>().ToTable("sales");
    }
}

public class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("audit_reg_schema");
        modelBuilder.MapWolverineEnvelopeStorage("audit_reg_schema");
        modelBuilder.Entity<AuditRecord>().ToTable("audits");
    }
}

public record RecordSale(Guid Id, string Product);

public class RecordSaleHandler
{
    public static void Handle(RecordSale cmd, SalesDbContext sales, AuditDbContext audit)
    {
        sales.Sales.Add(new Sale { Id = cmd.Id, Product = cmd.Product });
    }
}

public record RecordSaleViaStore(Guid Id, string Product);

public class RecordSaleViaStoreHandler
{
    public static void Handle(RecordSaleViaStore cmd, ISalesStore sales, AuditDbContext audit)
    {
        sales.Sales.Add(new Sale { Id = cmd.Id, Product = cmd.Product });
    }
}

public record SalesModuleOp(Guid Id);

public class SalesModuleHandler
{
    public static void Handle(SalesModuleOp cmd, SalesDbContext sales, AuditDbContext audit)
    {
        sales.Sales.Add(new Sale { Id = cmd.Id, Product = "module-sale" });
    }
}

public record AuditModuleOp(Guid Id);

public class AuditModuleHandler
{
    public static void Handle(AuditModuleOp cmd, SalesDbContext sales, AuditDbContext audit)
    {
        audit.AuditRecords.Add(new AuditRecord { Id = cmd.Id, Note = "module-audit" });
    }
}

public record SalesOnlyOp(Guid Id);

public class SalesOnlyHandler
{
    public static void Handle(SalesOnlyOp cmd, SalesDbContext sales, RatesDbContext rates)
    {
        sales.Sales.Add(new Sale { Id = cmd.Id, Product = "sales-only" });
    }
}

public record PrecedenceOp(Guid Id);

public class AttributeWinsHandler
{
    [Transactional(typeof(AuditDbContext))]
    public static void Handle(PrecedenceOp cmd, SalesDbContext sales, AuditDbContext audit)
    {
        audit.AuditRecords.Add(new AuditRecord { Id = cmd.Id, Note = "attr-wins" });
    }
}

public record SingleContextOp(Guid Id);

public class SingleContextHandler
{
    public static void Handle(SingleContextOp cmd, SalesDbContext sales)
    {
        sales.Sales.Add(new Sale { Id = cmd.Id, Product = "single" });
    }
}
