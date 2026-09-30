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
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.Bugs;

/// <summary>
/// Reproduction for GH-4712.
///
/// <para>
/// A handler that loads an entity through one of Wolverine's load attributes, mutates it, and relies on the
/// transactional middleware to save it never saves it. No <c>SaveChangesAsync</c> is generated, with
/// <c>AutoApplyTransactions()</c> or with an explicit <c>[Transactional]</c>, and nothing is logged.
/// </para>
///
/// <para>
/// <c>AutoApplyTransactions</c> asks each provider's <c>CanApply</c>, and EF Core's answers from
/// <c>chain.ServiceDependencies(...)</c>, which walks only the parameters and constructor dependencies of
/// <c>MethodCall</c> frames. Every load frame in the <c>[Entity]</c> family resolves its DbContext through
/// <c>IMethodVariables.FindVariable</c> at code-generation time instead, so it is a local in the generated
/// method and never a chain dependency -- the policy and the load frames look at two different things that
/// never meet. On a message handler there is a second, independent miss: the attributes' <c>Modify()</c> does
/// not run until <c>applyCustomizations</c>, long after <c>AutoApplyTransactions</c> has already decided.
/// </para>
///
/// <para>
/// The same blind spot exists in every provider's <c>CanApply</c>. It is only EF Core that loses data over
/// it, because EF Core's change tracker makes in-place mutation look like it should persist; a Marten session
/// is <c>DocumentTracking.None</c>, so mutating a loaded document was never going to save and the user has to
/// return a storage action anyway. Marten already carries the equivalent of the fix below for its own
/// at-risk attributes -- <c>ChainHasMartenSessionAttributes</c>, added for GH-2941, with the same reasoning.
/// </para>
///
/// <para>
/// Both assertions matter. The generated-source one names the defect exactly; the round trip proves the
/// emitted call actually commits. A results-only assertion would also pass if something else happened to
/// flush the change tracker.
/// </para>
/// </summary>
[Collection("sqlserver")]
public class Bug_4712_load_attributes_do_not_enroll_a_transaction : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RenamerHandler))
                    .IncludeType(typeof(LoadedPlanHandler))
                    .IncludeType(typeof(NoPersistenceHandler));

                opts.Services.AddDbContextWithWolverineIntegration<RenamerDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "bug4712");
                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
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

    private async Task<Guid> seedAsync()
    {
        var id = Guid.NewGuid();

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RenamerDbContext>();
        db.Items.RemoveRange(db.Items);
        db.Items.Add(new RenamerItem { Id = id, Name = "original" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }

    private async Task<string> nameOfAsync(Guid id)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RenamerDbContext>();
        var item = await db.Items.AsNoTracking()
            .SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        return item.Name;
    }

    [Theory]
    [InlineData(typeof(RenameEntity))]
    [InlineData(typeof(RenameEntityTransactional))]
    [InlineData(typeof(RenameFromEfCore))]
    [InlineData(typeof(RenameAll))]
    [InlineData(typeof(RenameFirstOrDefault))]
    [InlineData(typeof(RenameQueryable))]
    [InlineData(typeof(RenameSpecification))]
    public void a_load_attribute_makes_the_chain_transactional(Type messageType)
    {
        _host.GetRuntime().Handlers.HandlerFor(messageType);
        var chain = _host.GetRuntime().Handlers.ChainFor(messageType);
        chain.ShouldNotBeNull();

        var code = chain.SourceCode.ShouldNotBeNull();

        code.Contains("SaveChangesAsync").ShouldBeTrue(
            $"Loading through {messageType.Name}'s attribute counts as using the DbContext, so the transactional middleware has to apply. Generated source:\n{code}");
    }

    [Fact]
    public async Task entity_attribute_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameEntity(id));

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task entity_attribute_with_explicit_transactional_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameEntityTransactional(id));

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task from_ef_core_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameFromEfCore(id));

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task all_attribute_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameAll());

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task first_or_default_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameFirstOrDefault());

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task queryable_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameQueryable());

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task from_query_specification_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameSpecification());

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    /// <summary>
    /// The one shape in the reporter's matrix this fix does NOT reach. A query plan returned from a
    /// <c>Load</c> method carries no attribute -- EFCoreQuerySpecificationPolicy injects its frame during
    /// codegen, as an IMethodPreCompilationPolicy, long after every IChainPolicy has run and with nothing on
    /// the chain to detect. Asserted as it actually behaves so the gap is recorded rather than assumed
    /// fixed; see the note in the PR.
    /// </summary>
    [Fact]
    public void a_query_plan_returned_from_load_is_still_not_detected()
    {
        sourceFor<RenameLoadedPlan>().ShouldNotContain("SaveChangesAsync");
    }

    [Fact]
    public async Task taking_the_db_context_as_a_parameter_still_works()
    {
        // The control that already passed before the fix, and the workaround the reporter found
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameWithDbContext(id));

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task returning_a_storage_action_still_works()
    {
        // The other pre-existing workaround. Storage.Update enrolls the transaction explicitly, which is
        // why it was never affected.
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameWithStorageUpdate(id));

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public void a_handler_that_touches_no_persistence_is_left_alone()
    {
        // The negative control. Widening CanApply must not drag every handler into the transactional
        // middleware -- a chain with no DbContext dependency and no load attribute has nothing to commit.
        sourceFor<NoPersistenceNeeded>().ShouldNotContain("SaveChangesAsync");
    }
}

public record RenameEntity(Guid Id);

public record RenameEntityTransactional(Guid Id);

public record RenameFromEfCore(Guid Id);

public record RenameAll;

public record RenameFirstOrDefault;

public record RenameQueryable;

public record RenameSpecification;

public record RenameLoadedPlan;

public record RenameWithDbContext(Guid Id);

public record RenameWithStorageUpdate(Guid Id);

public record NoPersistenceNeeded;

public class RenamerItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class RenamerDbContext : DbContext
{
    public RenamerDbContext(DbContextOptions<RenamerDbContext> options) : base(options)
    {
    }

    public DbSet<RenamerItem> Items { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.MapWolverineEnvelopeStorage();

        modelBuilder.Entity<RenamerItem>(map =>
        {
            map.ToTable("renamer_items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}

// [WolverineIgnore] on all three: every other host bootstrapped from this assembly uses conventional
// discovery, and would otherwise pick these handlers up and fail resolving RenamerItem against a DbContext
// that does not map it. The fixture above reaches them through DisableConventionalDiscovery().IncludeType(),
// which bypasses HandlerQuery.Excludes entirely.
[WolverineIgnore]
public static class RenamerHandler
{
    public static void Handle(RenameEntity cmd, [Entity] RenamerItem item) => item.Name = "renamed";

    [Transactional]
    public static void Handle(RenameEntityTransactional cmd, [Entity] RenamerItem item) => item.Name = "renamed";

    public static void Handle(RenameFromEfCore cmd, [FromEfCore] RenamerItem item) => item.Name = "renamed";

    public static void Handle(RenameAll cmd, [All] IReadOnlyList<RenamerItem> items)
    {
        foreach (var item in items) item.Name = "renamed";
    }

    public static void Handle(RenameFirstOrDefault cmd, [FirstOrDefault] RenamerItem? item)
    {
        if (item != null) item.Name = "renamed";
    }

    public static async Task Handle(RenameQueryable cmd, [Queryable] IQueryable<RenamerItem> items)
    {
        foreach (var item in await items.ToListAsync()) item.Name = "renamed";
    }

    public static void Handle(RenameSpecification cmd,
        [FromQuerySpecification(typeof(AllRenamerItems))] IReadOnlyList<RenamerItem> items)
    {
        foreach (var item in items) item.Name = "renamed";
    }

    public static void Handle(RenameWithDbContext cmd, RenamerDbContext db)
    {
        var item = db.Set<RenamerItem>().Single(x => x.Id == cmd.Id);
        item.Name = "renamed";
    }

    public static Update<RenamerItem> Handle(RenameWithStorageUpdate cmd, [Entity] RenamerItem item)
    {
        item.Name = "renamed";
        return Storage.Update(item);
    }
}

public class AllRenamerItems : QueryListPlan<RenamerDbContext, RenamerItem>
{
    public override IQueryable<RenamerItem> Query(RenamerDbContext dbContext) => dbContext.Set<RenamerItem>();
}

[WolverineIgnore]
public static class LoadedPlanHandler
{
    public static AllRenamerItems Load(RenameLoadedPlan cmd) => new();

    public static void Handle(RenameLoadedPlan cmd, IReadOnlyList<RenamerItem> items)
    {
        foreach (var item in items) item.Name = "renamed";
    }
}

[WolverineIgnore]
public static class NoPersistenceHandler
{
    public static void Handle(NoPersistenceNeeded cmd)
    {
    }
}
