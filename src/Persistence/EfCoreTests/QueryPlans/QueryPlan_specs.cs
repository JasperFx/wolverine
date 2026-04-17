using Microsoft.EntityFrameworkCore;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Xunit;

namespace EfCoreTests.QueryPlans;

/// <summary>
/// Unit tests for the Phase 1 query-plan abstractions (GH-2505). These
/// exercise the base classes and extension method against EF Core's
/// in-memory provider — no SQL Server required.
/// </summary>
public class QueryPlan_specs : IAsyncLifetime
{
    private QueryPlanDbContext _db = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<QueryPlanDbContext>()
            .UseInMemoryDatabase($"querypans-{Guid.NewGuid():N}")
            .Options;

        _db = new QueryPlanDbContext(options);

        _db.Items.AddRange(
            new Item { Id = Guid.NewGuid(), Name = "Red Chair",   Approved = true  },
            new Item { Id = Guid.NewGuid(), Name = "Red Table",   Approved = false },
            new Item { Id = Guid.NewGuid(), Name = "Blue Sofa",   Approved = true  },
            new Item { Id = Guid.NewGuid(), Name = "Green Lamp",  Approved = true  });

        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task query_list_plan_returns_matching_rows()
    {
        var plan = new ItemsByNamePrefix("Red");
        var results = await plan.FetchAsync(_db, CancellationToken.None);

        results.Count.ShouldBe(2);
        results.ShouldAllBe(x => x.Name.StartsWith("Red"));
    }

    [Fact]
    public async Task query_list_plan_returns_empty_when_no_match()
    {
        var plan = new ItemsByNamePrefix("Zzz");
        var results = await plan.FetchAsync(_db, CancellationToken.None);

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task query_plan_returns_first_match()
    {
        var plan = new FirstApprovedItem();
        var result = await plan.FetchAsync(_db, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Approved.ShouldBeTrue();
    }

    [Fact]
    public async Task query_plan_returns_null_when_no_match()
    {
        // Delete everything, then run the plan
        _db.Items.RemoveRange(_db.Items);
        await _db.SaveChangesAsync();

        var plan = new FirstApprovedItem();
        var result = await plan.FetchAsync(_db, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task QueryByPlanAsync_extension_routes_to_the_plan()
    {
        var result = await _db.QueryByPlanAsync(new FirstApprovedItem());

        result.ShouldNotBeNull();
        result.Approved.ShouldBeTrue();
    }

    [Fact]
    public async Task QueryByPlanAsync_extension_works_with_list_plan()
    {
        var results = await _db.QueryByPlanAsync(new ItemsByNamePrefix("Red"));

        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task QueryByPlanAsync_throws_on_null_plan()
    {
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await _db.QueryByPlanAsync<QueryPlanDbContext, Item?>(null!));
    }

    [Fact]
    public async Task plan_can_compose_ordering_and_projection_inside_query()
    {
        var plan = new ItemsOrderedByName();
        var results = await plan.FetchAsync(_db, CancellationToken.None);

        // Alphabetical: Blue Sofa, Green Lamp, Red Chair, Red Table
        results.Select(x => x.Name).ShouldBe(new[]
        {
            "Blue Sofa", "Green Lamp", "Red Chair", "Red Table"
        });
    }

    [Fact]
    public async Task plan_parameters_via_constructor_flow_through_to_query()
    {
        // Verify that distinct parameter values yield distinct results — the
        // core claim of the specification pattern
        var red = await _db.QueryByPlanAsync(new ItemsByNamePrefix("Red"));
        var blue = await _db.QueryByPlanAsync(new ItemsByNamePrefix("Blue"));

        red.Count.ShouldBe(2);
        blue.Count.ShouldBe(1);
        blue.Single().Name.ShouldBe("Blue Sofa");
    }
}

// Test DbContext — distinct from SampleDbContext to avoid cross-test state
public class QueryPlanDbContext(DbContextOptions<QueryPlanDbContext> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Item>(map =>
        {
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
            map.Ignore(x => x.Events);
        });
    }
}

// Sample query plans — live in the test assembly to prove that user-defined
// plans in arbitrary assemblies work without any registration

public class ItemsByNamePrefix(string prefix) : QueryListPlan<QueryPlanDbContext, Item>
{
    public string Prefix { get; } = prefix;

    public override IQueryable<Item> Query(QueryPlanDbContext db)
        => db.Items.Where(x => x.Name.StartsWith(Prefix));
}

public class FirstApprovedItem : QueryPlan<QueryPlanDbContext, Item>
{
    public override IQueryable<Item> Query(QueryPlanDbContext db)
        => db.Items.Where(x => x.Approved).OrderBy(x => x.Name);
}

public class ItemsOrderedByName : QueryListPlan<QueryPlanDbContext, Item>
{
    public override IQueryable<Item> Query(QueryPlanDbContext db)
        => db.Items.OrderBy(x => x.Name);
}
