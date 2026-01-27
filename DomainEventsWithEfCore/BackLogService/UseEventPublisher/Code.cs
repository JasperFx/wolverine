using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace BackLogService.UseEventPublisher;

// Just assume that this little abstraction
// eventually relays the event messages to Wolverine
// or whatever messaging tool you're using
public interface IEventPublisher
{
    void Publish<T>(T @event);
}

public class SimpleRelayEventPublisher(IMessageBus Bus) : IEventPublisher
{
    public void Publish<T>(T @event)
    {
        // Just say no kids! This is a potential dead lock
        Bus.PublishAsync(@event).GetAwaiter().GetResult();
    }
}

// Patience, there's going to be some method to the madness in a bit
public class RecordingEventPublisher : OutgoingMessages, IEventPublisher
{
    public void Publish<T>(T @event)
    {
        Add(@event);
    }
}

public class CreateEventPublisher : SyncFrame
{
    public CreateEventPublisher()
    {
        Publisher = new Variable(typeof(RecordingEventPublisher), this);
        
        // Not proud of this
        Abstraction = new Variable(typeof(IEventPublisher), Publisher.Usage, this);
    }

    public Variable Abstraction { get;}

    public Variable Publisher { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Publisher.Usage} = new {typeof(RecordingEventPublisher).FullNameInCode()}();");
        Next?.GenerateCode(method, writer);
    }
}

public interface IBatchItemRepository
{
    Task<BacklogItem> LoadAsync(Guid id);
}

public class BatchItemRepository : IBatchItemRepository
{
    private readonly IEventPublisher _publisher;
    private readonly ItemsDbContext _dbContext;

    public BatchItemRepository(IEventPublisher publisher, ItemsDbContext dbContext)
    {
        _publisher = publisher;
        _dbContext = dbContext;
    }

    public async Task<BacklogItem> LoadAsync(Guid id)
    {
        var item = await _dbContext.BacklogItems.FindAsync(id);
        if (item != null)
        {
            item.Publisher = _publisher;
        }

        return item;
    }
}

public class ItemsDbContext : DbContext
{
    public DbSet<BacklogItem> BacklogItems { get; set; } 
    public DbSet<Sprint> Sprints { get; set; } 
}


// Using a Nullo just so you don't have potential
// NullReferenceExceptions
public class NulloEventPublisher : IEventPublisher
{
    public void Publish<T>(T @event)
    {
        // Do nothing.
    }
}

public abstract class Entity
{
    public IEventPublisher Publisher { get; set; } = new NulloEventPublisher();
}

public class BacklogItem : Entity
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string Description { get; private set; }
    
    // ZOMG, I forgot how annoying ORMs are. Use a document database
    // and stop worrying about making things virtual just for lazy loading
    public virtual Sprint Sprint { get; private set; }

    public void CommitTo(Sprint sprint)
    {
        Sprint = sprint;
        Publisher.Publish(new BackLotItemCommitted(Id, sprint.Id));
    }
}


public static class CommitToSprintHandler
{
    public static async Task Handle(
        CommitToSprint command,
        IBatchItemRepository repository,
        ItemsDbContext dbContext
    )
    {
        var item = await repository.LoadAsync(command.BacklogItemId);
        
        // I don't know how you'd get the Sprint object. Maybe you made
        // a different wrapper around DbContext just to attach the publisher instead
        // I do know that I absolutely despise having a separate repository type
        // for each entity type

        var sprint = await dbContext.Sprints.FindAsync(command.SprintId);
        
         
        // This method would cause an event to be published within
        // the BacklogItem object here that we need to gather up and
        // relay to Wolverine later
        item.CommitTo(sprint);
    }
}