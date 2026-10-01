using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;

namespace Wolverine.Http.Tests.EfCoreOnly;

// Every endpoint type in this file is [WolverineIgnore]d. Each one loads a StepEntityItem at chain
// construction, which throws in any host that does not map it -- and every other host pinned to this assembly
// discovers its endpoints. entity_on_step_methods_with_managed_multi_tenancy opts them back in through
// CustomizeHttpEndpointDiscovery, which is additive over the ignore.

public class StepEntityDbContext : DbContext
{
    public StepEntityDbContext(DbContextOptions<StepEntityDbContext> options) : base(options)
    {
    }

    public DbSet<StepEntityItem> Items => Set<StepEntityItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StepEntityItem>(map =>
        {
            map.ToTable("step_entity_items", "step_entity");
            map.HasKey(x => x.Id);
        });

        modelBuilder.Entity<StepEntityTag>(map =>
        {
            map.ToTable("step_entity_tags", "step_entity");
            map.HasKey(x => x.Id);
        });
    }
}

public class StepEntityItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

// Only StepEntityTagsEndpoint loads this, so its DbContext lookup is still uncached when that chain is built
public class StepEntityTag
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class StepEntityTagById : QueryPlan<StepEntityDbContext, StepEntityTag>
{
    private readonly Guid _id;

    public StepEntityTagById(Guid id)
    {
        _id = id;
    }

    public override IQueryable<StepEntityTag> Query(StepEntityDbContext dbContext)
    {
        return dbContext.Set<StepEntityTag>().Where(x => x.Id == _id);
    }
}

public class AllStepEntityTags : QueryListPlan<StepEntityDbContext, StepEntityTag>
{
    public override IQueryable<StepEntityTag> Query(StepEntityDbContext dbContext)
    {
        return dbContext.Set<StepEntityTag>();
    }
}

public class StepEntityItemById : QueryPlan<StepEntityDbContext, StepEntityItem>
{
    private readonly Guid _id;

    public StepEntityItemById(Guid id)
    {
        _id = id;
    }

    public override IQueryable<StepEntityItem> Query(StepEntityDbContext dbContext)
    {
        return dbContext.Items.Where(x => x.Id == _id);
    }
}

public class AllStepEntityItems : QueryListPlan<StepEntityDbContext, StepEntityItem>
{
    public override IQueryable<StepEntityItem> Query(StepEntityDbContext dbContext)
    {
        return dbContext.Items;
    }
}

// The entity is loaded ONLY by the step. The endpoint takes no DbContext, so nothing but the step's
// [Entity] tells the transactional middleware this chain writes.
[WolverineIgnore]
public static class StepEntityRenameEndpoint
{
    public static void Validate([Entity] StepEntityItem item)
    {
    }

    [WolverinePost("/step-entity/{id}/rename")]
    public static void Post([NotBody] StepEntityItem item)
    {
        item.Name = "renamed";
    }
}

// [Entity] on the endpoint itself -- the control that already worked
[WolverineIgnore]
public static class StepEntityRenameOnEndpoint
{
    [WolverinePost("/step-entity/{id}/rename-on-endpoint")]
    public static void Post([Entity] StepEntityItem item)
    {
        item.Name = "renamed";
    }
}

// Never transactional, so its DbContext is not built by the transactional middleware and has to come
// from the variable sources -- where it used to resolve the main database's DbContext out of the container
// instead of the request tenant's.
[WolverineIgnore]
public static class StepEntityReadEndpoint
{
    [NonTransactional]
    [WolverineGet("/step-entity/{id}/name")]
    public static string Get([Entity] StepEntityItem item)
    {
        return item.Name;
    }
}

[WolverineIgnore]
public static class StepEntityQueryPlanEndpoint
{
    public static StepEntityItemById Load(Guid id)
    {
        return new StepEntityItemById(id);
    }

    [WolverineGet("/step-entity/{id}/name-from-plan")]
    public static string Get(StepEntityItem? item)
    {
        return item?.Name ?? "missing";
    }
}

// Two plans on one endpoint are batched into one round trip, and both results flow into Validate
[WolverineIgnore]
public static class StepEntityTwoPlansEndpoint
{
    public static ProblemDetails Validate(StepEntityItem? item, IReadOnlyList<StepEntityItem> all)
    {
        return item == null || all.Count == 0
            ? new ProblemDetails { Status = 404 }
            : WolverineContinue.NoProblems;
    }

    [WolverinePost("/step-entity/{id}/two-plans")]
    public static void Post(
        [FromQuerySpecification(typeof(StepEntityItemById))] StepEntityItem? item,
        [FromQuerySpecification(typeof(AllStepEntityItems))] IReadOnlyList<StepEntityItem> all)
    {
        item!.Name = "renamed";
    }
}

// Takes the DbContext directly AND loads an entity type through plans. The first lookup of that entity's
// DbContext answered IDbContextBuilder<StepEntityDbContext> rather than StepEntityDbContext, so this chain
// appeared to use two DbContexts and the host failed to start.
[WolverineIgnore]
public static class StepEntityTagsEndpoint
{
    [WolverineGet("/step-entity/{id}/tags")]
    public static string Get(
        StepEntityDbContext db,
        [FromQuerySpecification(typeof(StepEntityTagById))] StepEntityTag? tag,
        [FromQuerySpecification(typeof(AllStepEntityTags))] IReadOnlyList<StepEntityTag> all)
    {
        return tag == null ? "missing" : $"{tag.Label} of {all.Count}";
    }
}
