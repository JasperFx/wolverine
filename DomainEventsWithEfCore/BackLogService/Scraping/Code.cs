using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence;

namespace BackLogService.Scraping;

#region sample_Entity_layer_super_type

// Of course, if you're into DDD, you'll probably 
// use many more marker interfaces than I do here, 
// but you do you and I'll do me in throwaway sample code
public abstract class Entity
{
    public List<object> Events { get; } = new();

    public void Publish(object @event)
    {
        Events.Add(@event);
    }
}

#endregion

#region sample_BacklogItem

public class BacklogItem : Entity
{
    public Guid Id { get; private set; }

    public string Description { get; private set; }
    public virtual Sprint Sprint { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    
    public void CommitTo(Sprint sprint)
    {
        Sprint = sprint;
        Publish(new BackLotItemCommitted(Id, sprint.Id));
    }
}

#endregion

public class ItemsDbContext : DbContext
{
    public DbSet<BacklogItem> BacklogItems { get; set; } 
    public DbSet<Sprint> Sprints { get; set; } 
}

public record CommitToSprint(Guid BacklogItemId, Guid SprintId);

#region sample_CommitToSprintHandler

public static class CommitToSprintHandler
{
    public static void Handle(
        CommitToSprint command,
        
        // There's a naming convention here about how
        // Wolverine "knows" the id for the BacklogItem
        // from the incoming command
        [Entity] BacklogItem item,
        [Entity] Sprint sprint
    )
    {
        // This method would cause an event to be published within
        // the BacklogItem object here that we need to gather up and
        // relay to Wolverine later
        item.CommitTo(sprint);
        
        // Wolverine's transactional middleware handles 
        // everything around SaveChangesAsync() and transactions
    }
}

#endregion

public static class RelayEvents
{
    public static async ValueTask PublishEventsAsync(DbContext dbContext, IMessageContext context)
    {
// Scrape the domain entities out of the Entity objects
// that were modified by the current DbContext
var eventMessages = dbContext
    .ChangeTracker
    .Entries()
    .Select(x => x.Entity)
    .OfType<Entity>()
    .SelectMany(x => x.Events);

foreach (var eventMessage in eventMessages)
{
    await context.PublishAsync(eventMessage);
}
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