using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;
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
    }
}

public class StepEntityItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
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
