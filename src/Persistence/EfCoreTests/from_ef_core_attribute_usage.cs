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
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests;

/// <summary>
///     <c>[FromEfCore]</c> is <c>[Entity]</c> pinned to EF Core, plus the two loading options that only EF Core has.
///     The inherited half is covered by the <c>[FromMarten]</c> suite and the existing <c>[Entity]</c> suites; what
///     is proved here is the EF-specific half.
/// </summary>
/// <remarks>
///     Each extra is asserted on the GENERATED SOURCE as well as at runtime. That is not belt-and-braces: the whole
///     failure mode this feature guards against is an <c>Include</c> or an <c>AsNoTracking</c> being silently dropped
///     on the floor, and a results-only assertion cannot always see the difference — a navigation can be populated by
///     change-tracker fixup rather than by the query, and a no-tracking read looks exactly like a tracked one until
///     something writes.
/// </remarks>
[Collection("sqlserver")]
public class from_ef_core_attribute_usage : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DepotHandler));

                opts.Services.AddDbContextWithWolverineIntegration<LogisticsDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "from_ef_core");
                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        db.ShipmentLines.RemoveRange(db.ShipmentLines);
        db.Shipments.RemoveRange(db.Shipments);
        db.Depots.RemoveRange(db.Depots);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Guid> seedDepot()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();

        var depot = new Depot { Id = Guid.NewGuid(), Name = "Topeka" };
        var shipment = new Shipment { Id = Guid.NewGuid(), DepotId = depot.Id, Tracking = "TRK-1" };
        var line = new ShipmentLine { Id = Guid.NewGuid(), ShipmentId = shipment.Id, Sku = "SKU-1" };

        db.Depots.Add(depot);
        db.Shipments.Add(shipment);
        db.ShipmentLines.Add(line);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return depot.Id;
    }

    private async Task<string?> nameOf(Guid id)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        var depot = await db.Depots.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        return depot?.Name;
    }

    private string sourceFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        var chain = _host.GetRuntime().Handlers.ChainFor<T>();
        chain.ShouldNotBeNull();

        var code = chain.SourceCode;
        code.ShouldNotBeNull();
        return code;
    }

    [Fact]
    public async Task a_plain_from_ef_core_behaves_like_entity()
    {
        var id = await seedDepot();

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadDepot(id));

        tracked.Sent.SingleMessage<DepotRead>().Name.ShouldBe("Topeka");
    }

    [Fact]
    public void a_plain_from_ef_core_still_uses_the_cheaper_find_async()
    {
        // No Include, no AsNoTracking, so nothing about the load should change from what [Entity] emits
        var code = sourceFor<ReadDepot>();

        code.ShouldContain("FindAsync<EfCoreTests.Depot>");
        code.ShouldNotContain("AsNoTracking");
    }

    [Fact]
    public async Task required_and_missing_still_stops_the_handler()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadDepot(Guid.NewGuid()));

        tracked.Sent.AllMessages().Any().ShouldBeFalse();
    }

    [Fact]
    public void as_no_tracking_switches_to_a_set_query_and_emits_as_no_tracking()
    {
        var code = sourceFor<RenameDepotWithoutTracking>();

        code.ShouldContain("AsNoTracking<EfCoreTests.Depot>");
        code.ShouldContain("FirstOrDefaultAsync<EfCoreTests.Depot>");
        code.ShouldContain("Property<System.Guid>(__entity, \"Id\")");

        // FindAsync cannot express either extra, so it must be gone
        code.ShouldNotContain("FindAsync<EfCoreTests.Depot>");
    }

    [Fact]
    public async Task as_no_tracking_really_does_detach_the_entity()
    {
        var id = await seedDepot();

        // The control: loaded the normal way, the entity is tracked, so a mutation is written
        await _host.InvokeAsync(new RenameDepot(id));
        DepotHandler.TrackedState.ShouldBe(EntityState.Unchanged);
        (await nameOf(id)).ShouldBe("renamed");

        // ...and with AsNoTracking the same handler shape leaves the entity detached, so the identical
        // mutation and SaveChangesAsync write nothing
        await _host.InvokeAsync(new RenameDepotWithoutTracking(id));
        DepotHandler.TrackedState.ShouldBe(EntityState.Detached);
        (await nameOf(id)).ShouldBe("renamed");
    }

    [Fact]
    public void a_single_include_is_emitted_as_a_string_include()
    {
        var code = sourceFor<ReadDepotWithShipments>();

        code.ShouldContain("Include<EfCoreTests.Depot>");
        code.ShouldContain("\"Shipments\"");
        code.ShouldContain("FirstOrDefaultAsync<EfCoreTests.Depot>");
    }

    [Fact]
    public async Task a_single_include_really_populates_the_navigation()
    {
        var id = await seedDepot();

        // The control: with no Include at all the collection comes back empty
        var without = await _host.InvokeMessageAndWaitAsync(new ReadDepot(id));
        without.Sent.SingleMessage<DepotRead>().ShipmentCount.ShouldBe(0);

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadDepotWithShipments(id));
        var read = tracked.Sent.SingleMessage<DepotRead>();

        read.ShipmentCount.ShouldBe(1);
        read.LineCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_dotted_include_path_chains_like_then_include()
    {
        var id = await seedDepot();

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadDepotWithLines(id));
        var read = tracked.Sent.SingleMessage<DepotRead>();

        read.ShipmentCount.ShouldBe(1);
        read.LineCount.ShouldBe(1);
    }

    [Fact]
    public async Task include_and_includes_combine_and_coexist_with_as_no_tracking()
    {
        var id = await seedDepot();

        var code = sourceFor<ReadDepotEverything>();
        code.ShouldContain("\"Shipments\"");
        code.ShouldContain("\"Shipments.Lines\"");
        code.ShouldContain("AsNoTracking<EfCoreTests.Depot>");

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadDepotEverything(id));
        var read = tracked.Sent.SingleMessage<DepotRead>();

        read.ShipmentCount.ShouldBe(1);
        read.LineCount.ShouldBe(1);
    }
}

/// <summary>
///     The refusals. Every one of these would otherwise be a handler that compiles, runs, and quietly returns an
///     entity that is not what the developer asked for.
/// </summary>
[Collection("sqlserver")]
public class from_ef_core_refuses_what_it_cannot_honor
{
    private static async Task compile<T>(Type handlerType)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType);

                opts.Services.AddDbContextWithWolverineIntegration<LogisticsDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "from_ef_core");
                opts.UseEntityFrameworkCoreTransactions();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        host.GetRuntime().Handlers.HandlerFor<T>();
    }

    [Fact]
    public async Task an_include_path_that_names_no_navigation_fails_at_codegen()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            compile<ReadDepotWithBadInclude>(typeof(BadIncludeHandler)));

        ex.Message.ShouldContain("[FromEfCore]");
        ex.Message.ShouldContain("'depot'");
        ex.Message.ShouldContain("Cargo");
        ex.Message.ShouldContain("Known navigations there are: Shipments");
    }

    [Fact]
    public async Task a_broken_second_segment_of_a_dotted_path_fails_at_codegen()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            compile<ReadDepotWithBadThenInclude>(typeof(BadThenIncludeHandler)));

        ex.Message.ShouldContain("Shipments.Crates");
        ex.Message.ShouldContain("\"Crates\" is not a navigation property on EfCoreTests.Shipment");
    }

    /// <summary>
    ///     The second failure mode an explicit attribute can have: the store is registered, but it does not know
    ///     this type. A plain <c>[Entity]</c> would have fallen through to some other provider — which is exactly
    ///     the accident these attributes exist to prevent.
    /// </summary>
    [Fact]
    public async Task an_unmapped_entity_type_fails_naming_ef_core_and_the_type()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            compile<ReadUnmapped>(typeof(UnmappedHandler)));

        ex.Message.ShouldContain("[FromEfCore]");
        ex.Message.ShouldContain("EF Core is registered with this application, but it does not persist");
        ex.Message.ShouldContain("EfCoreTests.NotMappedAnywhere");
        ex.Message.ShouldContain("No registered DbContext maps");
    }
}

public class Depot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public List<Shipment> Shipments { get; set; } = [];
}

public class Shipment
{
    public Guid Id { get; set; }
    public Guid DepotId { get; set; }
    public string Tracking { get; set; } = null!;
    public List<ShipmentLine> Lines { get; set; } = [];
}

public class ShipmentLine
{
    public Guid Id { get; set; }
    public Guid ShipmentId { get; set; }
    public string Sku { get; set; } = null!;
}

public class NotMappedAnywhere
{
    public Guid Id { get; set; }
}

public class LogisticsDbContext : DbContext
{
    public LogisticsDbContext(DbContextOptions<LogisticsDbContext> options) : base(options)
    {
    }

    public DbSet<Depot> Depots { get; set; } = null!;
    public DbSet<Shipment> Shipments { get; set; } = null!;
    public DbSet<ShipmentLine> ShipmentLines { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.MapWolverineEnvelopeStorage();

        modelBuilder.Entity<Depot>(map =>
        {
            map.ToTable("depots");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
            map.HasMany(x => x.Shipments).WithOne().HasForeignKey(x => x.DepotId);
        });

        modelBuilder.Entity<Shipment>(map =>
        {
            map.ToTable("shipments");
            map.HasKey(x => x.Id);
            map.Property(x => x.Tracking);
            map.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ShipmentId);
        });

        modelBuilder.Entity<ShipmentLine>(map =>
        {
            map.ToTable("shipment_lines");
            map.HasKey(x => x.Id);
            map.Property(x => x.Sku);
        });
    }
}

public record ReadDepot(Guid Id);

public record ReadDepotWithShipments(Guid Id);

public record ReadDepotWithLines(Guid Id);

public record ReadDepotEverything(Guid Id);

public record RenameDepot(Guid Id);

public record RenameDepotWithoutTracking(Guid Id);

public record DepotRead(string Name, int ShipmentCount, int LineCount);

[WolverineIgnore]
public static class DepotHandler
{
    public static EntityState? TrackedState { get; set; }

    public static DepotRead Handle(ReadDepot command, [FromEfCore] Depot depot)
        => read(depot);

    public static DepotRead Handle(ReadDepotWithShipments command,
        [FromEfCore(Include = "Shipments")] Depot depot)
        => read(depot);

    public static DepotRead Handle(ReadDepotWithLines command,
        [FromEfCore(Include = "Shipments.Lines")] Depot depot)
        => read(depot);

    public static DepotRead Handle(ReadDepotEverything command,
        [FromEfCore(AsNoTracking = true, Include = "Shipments", Includes = new[] { "Shipments.Lines" })]
        Depot depot)
        => read(depot);

    // Both of these are deliberately identical apart from the AsNoTracking flag: same mutation, same
    // explicit SaveChangesAsync, same DbContext. Only the tracking state differs, and only one of them
    // writes anything.
    public static async Task Handle(RenameDepot command, [FromEfCore] Depot depot, LogisticsDbContext db,
        CancellationToken token)
    {
        TrackedState = db.Entry(depot).State;
        depot.Name = "renamed";
        await db.SaveChangesAsync(token);
    }

    public static async Task Handle(RenameDepotWithoutTracking command,
        [FromEfCore(AsNoTracking = true)] Depot depot, LogisticsDbContext db, CancellationToken token)
    {
        TrackedState = db.Entry(depot).State;
        depot.Name = "never persisted";
        await db.SaveChangesAsync(token);
    }

    public static void Handle(DepotRead read)
    {
    }

    private static DepotRead read(Depot depot)
        => new(depot.Name, depot.Shipments.Count, depot.Shipments.Sum(x => x.Lines.Count));
}

public record ReadDepotWithBadInclude(Guid Id);

[WolverineIgnore]
public static class BadIncludeHandler
{
    public static void Handle(ReadDepotWithBadInclude command, [FromEfCore(Include = "Cargo")] Depot depot)
    {
    }
}

public record ReadDepotWithBadThenInclude(Guid Id);

[WolverineIgnore]
public static class BadThenIncludeHandler
{
    public static void Handle(ReadDepotWithBadThenInclude command,
        [FromEfCore(Include = "Shipments.Crates")] Depot depot)
    {
    }
}

public record ReadUnmapped(Guid Id);

[WolverineIgnore]
public static class UnmappedHandler
{
    public static void Handle(ReadUnmapped command, [FromEfCore] NotMappedAnywhere thing)
    {
    }
}
