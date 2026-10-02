using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;

namespace Wolverine.Http.Tests.EfCoreOnly;

// [WolverineIgnore]d so other hosts scanning this assembly don't build chains for an entity they don't map.
// entity_on_step_methods_with_managed_multi_tenancy opts them back in.

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

// Loaded only by StepEntityTagsEndpoint, so its DbContext lookup is uncached when that chain is built
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

[WolverineIgnore]
public static class StepEntityRenameOnEndpoint
{
    [WolverinePost("/step-entity/{id}/rename-on-endpoint")]
    public static void Post([Entity] StepEntityItem item)
    {
        item.Name = "renamed";
    }
}

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

public record StepEntityReminder(Guid Id);

[WolverineIgnore]
public static class StepEntityReminderHandler
{
    public static void Handle(StepEntityReminder message)
    {
    }
}

[WolverineIgnore]
public static class StepEntityScheduleEndpoint
{
    [NonTransactional]
    [WolverinePost("/step-entity/{id}/schedule")]
    public static Task Post(Guid id, StepEntityDbContext db, IMessageBus bus)
    {
        return bus.ScheduleAsync(new StepEntityReminder(id), TimeSpan.FromHours(1)).AsTask();
    }
}
