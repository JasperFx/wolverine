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

namespace EfCoreTests;

// Reproduces the scenario from the "per-handler transactional DbContext" architecture review:
// a single handler depends on two different DbContext types - one that owns the write/aggregate
// and must be enrolled in the transaction + outbox, and one that is only used for read-only
// lookups and must never be wrapped in a transaction. Today `EFCorePersistenceFrameProvider.
// DetermineDbContextType(IChain, ...)` throws `InvalidOperationException` the moment it sees more
// than one DbContext-shaped dependency (EFCorePersistenceFrameProvider.cs:497-501) - it has no way
// to know which one is "the" transactional context for this chain. `[Transactional(typeof(TDbContext))]`
// disambiguates that per-chain.
public class transactional_dbcontext_selection_tests
{
    [Fact]
    public async Task explicit_dbcontext_type_disambiguates_and_only_enrolls_that_context()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("widget_selection_schema");
            await conn.CreateCommand(
                    """
                    CREATE SCHEMA "widget_selection_schema";
                    CREATE TABLE widget_selection_schema.widgets (
                        "Id" uuid PRIMARY KEY,
                        "Name" text NOT NULL
                    );
                    """)
                .ExecuteNonQueryAsync();
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<WidgetDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                // A second, unrelated DbContext used only for read-only lookups. It must never
                // participate in the transaction/outbox even though it's also DbContext-shaped.
                opts.Services.AddDbContext<LookupDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_widget_sel");

                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<CreateWidgetHandler>();
            }).StartAsync();

        // [Transactional]-attributed chains apply their attribute lazily on first compile, so force
        // it the same way transaction_middleware_mode_tests.cs does before inspecting Middleware.
        host.GetRuntime().Handlers.HandlerFor<CreateWidget>();
        var chain = host.GetRuntime().Handlers.ChainFor<CreateWidget>();
        chain.ShouldNotBeNull();

        // Only the tagged DbContext should be enrolled in the transaction.
        var enrollFrames = chain.Middleware.OfType<EnrollDbContextInTransaction>().ToArray();
        enrollFrames.Length.ShouldBe(1);

        var saveChanges = chain.Postprocessors.OfType<JasperFx.CodeGeneration.Frames.MethodCall>()
            .Single(x => x.Method.Name == nameof(DbContext.SaveChangesAsync));
        saveChanges.HandlerType.ShouldBe(typeof(WidgetDbContext));

        var widgetId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new CreateWidget(widgetId, "gizmo"));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WidgetDbContext>();
        (await db.Widgets.AnyAsync(w => w.Id == widgetId)).ShouldBeTrue();
    }

    [Fact]
    public async Task explicit_dbcontext_type_that_is_not_a_dependency_throws_clear_error()
    {
        async Task startBadHost()
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;

                    opts.Services.AddDbContextWithWolverineIntegration<WidgetDbContext>(x =>
                        x.UseNpgsql(Servers.PostgresConnectionString));

                    opts.Services.AddDbContext<LookupDbContext>(x =>
                        x.UseNpgsql(Servers.PostgresConnectionString));

                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_widget_sel_err");

                    opts.UseEntityFrameworkCoreTransactions();
                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType<CreateWidgetWithBadTagHandler>();
                }).StartAsync();

            // [Transactional] is applied lazily on first compile - force it.
            host.GetRuntime().Handlers.HandlerFor<CreateWidgetWithBadTag>();
        }

        // The attribute references LookupDbContext, but this handler doesn't depend on it - only
        // on WidgetDbContext. That mismatch must fail loudly and name the offending type, rather
        // than silently falling back to some default DbContext.
        var ex = await Should.ThrowAsync<InvalidOperationException>(startBadHost);
        ex.Message.ShouldContain("is not one of this chain's dependencies");
    }

    // Clean Architecture handlers can't reference the concrete DbContext type at all (it lives in
    // Infrastructure). They depend on a repository abstraction registered via
    // `WithDbContextAbstraction<TAbstraction, TDbContext>()` instead, so [Transactional] must accept
    // that abstraction type and resolve it to the concrete DbContext internally.
    [Fact]
    public async Task explicit_dbcontext_type_can_be_a_registered_abstraction()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("gadget_selection_schema");
            await conn.CreateCommand(
                    """
                    CREATE SCHEMA "gadget_selection_schema";
                    CREATE TABLE gadget_selection_schema.gadgets (
                        "Id" uuid PRIMARY KEY,
                        "Name" text NOT NULL
                    );
                    """)
                .ExecuteNonQueryAsync();
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<GadgetDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));
                opts.Services.AddScoped<IGadgetRepository>(sp => sp.GetRequiredService<GadgetDbContext>());

                opts.Services.AddDbContext<LookupDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine_gadget_sel");

                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<IGadgetRepository, GadgetDbContext>();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<CreateGadgetHandler>();
            }).StartAsync();

        host.GetRuntime().Handlers.HandlerFor<CreateGadget>();
        var chain = host.GetRuntime().Handlers.ChainFor<CreateGadget>();
        chain.ShouldNotBeNull();

        chain.Middleware.OfType<EnrollDbContextInTransaction>().ToArray().Length.ShouldBe(1);

        var saveChanges = chain.Postprocessors.OfType<JasperFx.CodeGeneration.Frames.MethodCall>()
            .Single(x => x.Method.Name == nameof(DbContext.SaveChangesAsync));
        saveChanges.HandlerType.ShouldBe(typeof(GadgetDbContext));

        var gadgetId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new CreateGadget(gadgetId, "sprocket"));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GadgetDbContext>();
        (await db.Gadgets.AnyAsync(g => g.Id == gadgetId)).ShouldBeTrue();
    }
}

public class Widget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class WidgetDbContext(DbContextOptions<WidgetDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("widget_selection_schema");
        modelBuilder.MapWolverineEnvelopeStorage("widget_selection_schema");
        modelBuilder.Entity<Widget>().ToTable("widgets");
    }
}

// Deliberately has no entities mapped that overlap with WidgetDbContext - it exists purely to
// prove a second, unrelated DbContext dependency doesn't have to participate in the transaction.
public class LookupDbContext(DbContextOptions<LookupDbContext> options) : DbContext(options);

public record CreateWidget(Guid Id, string Name);

public class CreateWidgetHandler
{
    [Transactional(typeof(WidgetDbContext))]
    public static void Handle(CreateWidget cmd, WidgetDbContext widgets, LookupDbContext lookup)
    {
        widgets.Widgets.Add(new Widget { Id = cmd.Id, Name = cmd.Name });
    }
}

public record CreateWidgetWithBadTag(Guid Id);

public class CreateWidgetWithBadTagHandler
{
    [Transactional(typeof(LookupDbContext))]
    public static void Handle(CreateWidgetWithBadTag cmd, WidgetDbContext widgets)
    {
        widgets.Widgets.Add(new Widget { Id = cmd.Id, Name = "n/a" });
    }
}

public class GadgetEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

// The Application-layer-facing abstraction - the only thing the handler is allowed to depend on.
public interface IGadgetRepository
{
    DbSet<GadgetEntity> Gadgets { get; }
}

public class GadgetDbContext(DbContextOptions<GadgetDbContext> options) : DbContext(options), IGadgetRepository
{
    public DbSet<GadgetEntity> Gadgets => Set<GadgetEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("gadget_selection_schema");
        modelBuilder.MapWolverineEnvelopeStorage("gadget_selection_schema");
        modelBuilder.Entity<GadgetEntity>().ToTable("gadgets");
    }
}

public record CreateGadget(Guid Id, string Name);

public class CreateGadgetHandler
{
    [Transactional(typeof(IGadgetRepository))]
    public static void Handle(CreateGadget cmd, IGadgetRepository gadgets, LookupDbContext lookup)
    {
        gadgets.Gadgets.Add(new GadgetEntity { Id = cmd.Id, Name = cmd.Name });
    }
}
