using IntegrationTests;
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
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace EfCoreTests;

// Companion to transactional_dbcontext_selection_tests.cs. That file proves the [Transactional(typeof(X))]
// path; this one proves the alternative the architecture review asked for: reusing the provider-agnostic
// [Storage(typeof(X))] attribute to designate which DbContext owns the transaction on a handler that
// depends on more than one. It also pins the "no magic" contract: an ambiguous chain with no explicit
// designation fails fast with a message that names both escape hatches.
public class storage_dbcontext_selection_tests
{
    [Fact]
    public async Task storage_attribute_disambiguates_and_only_enrolls_that_context()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("invoice_storage_schema");
            await conn.CreateCommand(
                    """
                    CREATE SCHEMA "invoice_storage_schema";
                    CREATE TABLE invoice_storage_schema.invoices (
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

                opts.Services.AddDbContextWithWolverineIntegration<InvoiceDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                // A second, read-only DbContext dependency that must never be enrolled.
                opts.Services.AddDbContext<PricingDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_invoice_storage");

                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<CreateInvoiceHandler>();
            }).StartAsync();

        host.GetRuntime().Handlers.HandlerFor<CreateInvoice>();
        var chain = host.GetRuntime().Handlers.ChainFor<CreateInvoice>();
        chain.ShouldNotBeNull();

        // [Storage(typeof(InvoiceDbContext))] set AncillaryStoreType; DetermineDbContextType honored it,
        // so only InvoiceDbContext is enrolled — PricingDbContext stays an ordinary read dependency.
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ToArray().Length.ShouldBe(1);

        var saveChanges = chain.Postprocessors.OfType<MethodCall>()
            .Single(x => x.Method.Name == nameof(DbContext.SaveChangesAsync));
        saveChanges.HandlerType.ShouldBe(typeof(InvoiceDbContext));

        var invoiceId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new CreateInvoice(invoiceId, "opening balance"));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        (await db.Invoices.AnyAsync(i => i.Id == invoiceId)).ShouldBeTrue();
    }

    [Fact]
    public async Task storage_attribute_that_is_not_a_dependency_throws_clear_error()
    {
        async Task startBadHost()
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;

                    opts.Services.AddDbContextWithWolverineIntegration<InvoiceDbContext>(x =>
                        x.UseNpgsql(Servers.PostgresConnectionString));
                    opts.Services.AddDbContext<PricingDbContext>(x =>
                        x.UseNpgsql(Servers.PostgresConnectionString));

                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_invoice_storage_err");

                    opts.UseEntityFrameworkCoreTransactions();
                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType<CreateInvoiceWithBadStorageHandler>();
                }).StartAsync();

            host.GetRuntime().Handlers.HandlerFor<CreateInvoiceWithBadStorage>();
        }

        var ex = await Should.ThrowAsync<InvalidOperationException>(startBadHost);
        ex.Message.ShouldContain("is not one of this chain's dependencies");
    }

    [Fact]
    public async Task ambiguous_multi_dbcontext_without_designation_throws_helpful_error()
    {
        async Task startBadHost()
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;

                    opts.Services.AddDbContextWithWolverineIntegration<InvoiceDbContext>(x =>
                        x.UseNpgsql(Servers.PostgresConnectionString));
                    // A second Wolverine-enabled context so there is genuinely no single default.
                    opts.Services.AddDbContextWithWolverineIntegration<AuditingDbContext>(x =>
                        x.UseNpgsql(Servers.PostgresConnectionString));

                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_ambiguous");

                    opts.UseEntityFrameworkCoreTransactions();
                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType<AmbiguousHandler>();
                }).StartAsync();

            host.GetRuntime().Handlers.HandlerFor<AmbiguousCommand>();
        }

        // No [Transactional]/[Storage] designation and two candidates => no magic, fail fast, and the
        // message must point the user at both escape hatches.
        var ex = await Should.ThrowAsync<InvalidOperationException>(startBadHost);
        ex.Message.ShouldContain("multiple DbContext types detected");
        ex.Message.ShouldContain("[Transactional(typeof(YourDbContext))]");
        ex.Message.ShouldContain("[Storage(typeof(YourDbContext))]");
    }
}

public class Invoice
{
    public Guid Id { get; set; }
    public string Memo { get; set; } = string.Empty;
}

public class InvoiceDbContext(DbContextOptions<InvoiceDbContext> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("invoice_storage_schema");
        modelBuilder.MapWolverineEnvelopeStorage("invoice_storage_schema");
        modelBuilder.Entity<Invoice>().ToTable("invoices");
    }
}

// Read-only lookup context: no overlapping entities, exists only to add a second DbContext dependency.
public class PricingDbContext(DbContextOptions<PricingDbContext> options) : DbContext(options);

// A second Wolverine-enabled write context used to force genuine ambiguity.
public class AuditingDbContext(DbContextOptions<AuditingDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("invoice_storage_schema");
        modelBuilder.MapWolverineEnvelopeStorage("invoice_storage_schema");
    }
}

public record CreateInvoice(Guid Id, string Memo);

[WolverineIgnore]
public class CreateInvoiceHandler
{
    [Storage(typeof(InvoiceDbContext))]
    public static void Handle(CreateInvoice cmd, InvoiceDbContext invoices, PricingDbContext pricing)
    {
        invoices.Invoices.Add(new Invoice { Id = cmd.Id, Memo = cmd.Memo });
    }
}

public record CreateInvoiceWithBadStorage(Guid Id);

[WolverineIgnore]
public class CreateInvoiceWithBadStorageHandler
{
    // Names PricingDbContext, which this handler does not depend on.
    [Storage(typeof(PricingDbContext))]
    public static void Handle(CreateInvoiceWithBadStorage cmd, InvoiceDbContext invoices)
    {
        invoices.Invoices.Add(new Invoice { Id = cmd.Id, Memo = "n/a" });
    }
}

public record AmbiguousCommand(Guid Id);

[WolverineIgnore]
public class AmbiguousHandler
{
    public static void Handle(AmbiguousCommand cmd, InvoiceDbContext invoices, AuditingDbContext audit)
    {
        invoices.Invoices.Add(new Invoice { Id = cmd.Id, Memo = "ambiguous" });
    }
}
