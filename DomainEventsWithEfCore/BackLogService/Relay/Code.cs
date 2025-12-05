using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence;


public class BacklogItem 
{
    public Guid Id { get; private set; }

    public string Description { get; private set; }
    public virtual Sprint Sprint { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    
    // The exact return type isn't hugely important here
    public object[] CommitTo(Sprint sprint)
    {
        Sprint = sprint;
        return [new BackLotItemCommitted(Id, sprint.Id)];
    }
}

public class ItemsDbContext : DbContext
{
    public DbSet<BacklogItem> BacklogItems { get; set; } 
    public DbSet<Sprint> Sprints { get; set; } 
}

public record CommitToSprint(Guid BacklogItemId, Guid SprintId);

public static class CommitToSprintHandler
{
    public static object[] Handle(
        CommitToSprint command,
        
        // There's a naming convention here about how
        // Wolverine "knows" the id for the BacklogItem
        // from the incoming command
        [Entity] BacklogItem item,
        [Entity] Sprint sprint
        )
    {
        return item.CommitTo(sprint);
    }
}

public class RelayEventsPolicy : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            var deps = chain
                .ServiceDependencies(container, [])
                .Where(x => x.CanBeCastTo(typeof(DbContext))).ToArray();
        }
    }
}

public record BackLotItemCommitted(Guid ItemId, Guid SprintId);

public class Sprint
{
    public Guid Id { get; set; }
}