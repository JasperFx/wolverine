# Publishing Domain Events <Badge type="tip" text="5.6" />

::: info
This section is all about using the traditional .NET "Domain Events" approach commonly used with EF Core,
but piping the domain events raised through Wolverine messaging.
:::

Wolverine's integration with EF Core also includes support for the typical "Domain Events" publishing that
folks like to do with EF Core `DbContext` classes and some sort of `DomainEntity` [layer supertype](https://martinfowler.com/eaaCatalog/layerSupertype.html). 

See Jeremy's post [“Classic” .NET Domain Events with Wolverine and EF Core](https://jeremydmiller.com/2025/12/04/classic-net-domain-events-with-wolverine-and-ef-core/) for much more background.

Jumping right into an example, let's say that you like to use a layer supertype in your domain model that
gives your `Entity` types a chance to "raise" domain events like this one:

<!-- snippet: sample_Entity_layer_super_type -->
<a id='snippet-sample_Entity_layer_super_type'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/DomainEventsWithEfCore/BackLogService/Scraping/Code.cs#L11-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_Entity_layer_super_type' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Now, let's say we're building some kind of software project planning software (as if the world doesn't have enough
"Jira but different" applications) where we'll have an entity like this one:

<!-- snippet: sample_BacklogItem -->
<a id='snippet-sample_BacklogItem'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/DomainEventsWithEfCore/BackLogService/Scraping/Code.cs#L28-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_BacklogItem' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Let’s utilize this a little bit within a Wolverine handler, first with explicit code:

<!-- snippet: sample_CommitToSprintHandler -->
<a id='snippet-sample_CommitToSprintHandler'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/DomainEventsWithEfCore/BackLogService/Scraping/Code.cs#L55-L79' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_CommitToSprintHandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Now, let’s add some Wolverine configuration to just make this pattern work:

```csharp
builder.Host.UseWolverine(opts =>
{
    // Setting up Sql Server-backed message storage
    // This requires a reference to Wolverine.SqlServer
    opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
 
    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();
     
    // THIS IS A NEW API IN Wolverine 5.6!
    opts.PublishDomainEventsFromEntityFrameworkCore<Entity>(x => x.Events);
 
    // Enrolling all local queues into the
    // durable inbox/outbox processing
    opts.Policies.UseDurableLocalQueues();
});
```

In the Wolverine configuration above, the EF Core transactional middleware now “knows” how to 
scrape out possible domain events from the active DbContext.ChangeTracker and publish them through 
Wolverine. Moreover, the [EF Core transactional middleware](/guide/durability/efcore/transactional-middleware) is doing all the operation ordering for 
you so that the events are enqueued as outgoing messages as part of the transaction and potentially 
persisted to the transactional inbox or outbox (depending on configuration) before the transaction is committed.

::: tip
To make this as clear as possible, this approach is completely reliant on the EF Core transactional middleware.
:::

Also note that this domain event “scraping” is also supported and tested with the `IDbContextOutbox<T>` service 
if you want to use this in application code outside of Wolverine message handlers or HTTP endpoints.

If I were building a system that embeds domain event publishing directly in domain model entity classes, I would prefer this approach. But, let’s talk about another option that will not require any changes to Wolverine…

## Relay Events from Entity to Wolverine Cascading Messages

In this approach, which I’m granting that some people won’t like at all, we’ll simply pipe the event messages from the domain entity right to 
Wolverine and utilize Wolverine’s [cascading message](/guide/handlers/cascading) feature.

This time I’m going to change the BacklogItem entity class to something like this:

```csharp
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
```

With the handler signature:

```csharp
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
```

The approach above let’s you make the handler be a single pure function which is always great for unit 
testing, eliminates the need to do any customization of the DbContext type, makes it unnecessary to 
bother with any kind of IEventPublisher interface, and let’s you keep the logic for what event messages 
should be raised completely in your domain model entity types.

I’d also argue that this approach makes it more clear to later developers that “hey, additional messages may be published as part of handling the CommitToSprint command” and I think that’s invaluable. I’ll harp on this more later, but I think the traditional, MediatR-flavored approach to domain events from the first 
example at the top makes application code harder to reason about and therefore more buggy over time.

## Embedding IEventPublisher into the Entities

Lastly, let’s move to what I think is my least favorite approach that I will from this moment be recommending against for any JasperFx clients but is now completely supported by Wolverine. 
Let’s use an `IEventPublisher` interface like this:

```csharp
// Just assume that this little abstraction
// eventually relays the event messages to Wolverine
// or whatever messaging tool you're using
public interface IEventPublisher
{
    void Publish<T>(T @event) where T : IDomainEvent;
}
 
// Using a Nullo just so you don't have potential
// NullReferenceExceptions
public class NulloEventPublisher : IEventPublisher
{
    public void Publish<T>(T @event) where T : IDomainEvent
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
```

Now, on to a Wolverine implementation for this pattern. You’ll need to do just a couple things. First, add this line of configuration to Wolverine, and note there are no generic arguments here:

```csharp
// This will set you up to scrape out domain events in the
// EF Core transactional middleware using a special service
// I'm just about to explain
opts.PublishDomainEventsFromEntityFrameworkCore();
```

Now, build a real implementation of that IEventPublisher interface above:

```csharp
public class EventPublisher(OutgoingDomainEvents Events) : IEventPublisher
{
    public void Publish<T>(T e) where T : IDomainEvent
    {
        Events.Add(e);
    }
}
```

`OutgoingDomainEvents` is a service from the WolverineFx.EntityFrameworkCore Nuget that is registered as Scoped by the usage of the EF Core transactional middleware. Next, register your custom IEventPublisher with the Scoped lifecycle:

```csharp
opts.Services.AddScoped<IEventPublisher, EventPublisher>();
```

How you wire up `IEventPublisher` to your domain entities getting loaded out of the your EF Core `DbContext`? Frankly, I don’t want to know. Maybe a repository abstraction around your DbContext types? Dunno. I hate that kind of thing in code, but I perfectly trust *you* to do that and to not make me see that code.

What’s important is that within a message handler or HTTP endpoint, if you resolve the `IEventPublisher` through DI and use 
the EF Core transactional middleware, the domain events published to that interface will be piped correctly into Wolverine’s active messaging context.

Likewise, if you are using `IDbContextOutbox<T>`, the domain events published to `IEventPublisher` will be correctly piped to Wolverine if you:

1. Pull both `IEventPublisher` and `IDbContextOutbox<T>` from the same scoped service provider (nested container in Lamar / StructureMap parlance)
2. Call `IDbContextOutbox<T>.SaveChangesAndFlushMessagesAsync()`

3. So, we’re going to have to do some sleight of hand to keep your domain entities synchronous

Last note, in unit testing you might use a stand in “Spy” like this:
