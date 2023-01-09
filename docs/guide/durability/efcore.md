# Entity Framework Core Integration

Wolverine supports [Entity Framework Core](https://learn.microsoft.com/en-us/ef/core/) through the `WolverineFx.EntityFrameworkCore` Nuget.
There's only a handful of touch points to EF Core that you need to be aware of:

* Transactional middleware - Wolverine will both call `DbContext.SaveChangesAsync()` and flush any persisted messages for you
* EF Core as a saga storage mechanism - As long as one of your registered `DbContext` services has a mapping for the stateful saga type 
* Outbox integration - Wolverine can use directly use a `DbContext` that has mappings for the Wolverine durable messaging, or at least use the database connection and current database transaction from a `DbContext` as part of durable, outbox message persistence.


## Registering Transactional Middleware and Saga Support

::: tip
It's no longer mandatory to use the `[Transactional]` attribute to enroll in transactional middleware. 
:::

Support for using Wolverine transactional middleware requires an explicit registration on `WolverineOptions`
shown below (it's an extension method):

<!-- snippet: sample_registering_efcore_middleware -->
<a id='snippet-sample_registering_efcore_middleware'></a>
```cs
builder.Host.UseWolverine(opts =>
{
    // Setting up Sql Server-backed message storage
    // This requires a reference to Wolverine.SqlServer
    opts.PersistMessagesWithSqlServer(connectionString);

    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L24-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_efcore_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Wolverine (really Lamar) can detect that your handler method uses a `DbContext` if it's a method argument,
a dependency of any service injected as a method argument, or a dependency of any service injected as a constructor
argument of the handler class.
:::

That will enroll EF Core as both a strategy for stateful saga support and for transactional middleware. With this
option added, Wolverine will wrap transactional middleware around any message handler that has a dependency on any
type of `DbContext` like this one:

<!-- snippet: sample_handler_using_efcore -->
<a id='snippet-sample_handler_using_efcore'></a>
```cs
public static ItemCreated Handle(
    // This would be the message
    CreateItemCommand command,

    // Any other arguments are assumed
    // to be service dependencies
    ItemsDbContext db)
{
    // Create a new Item entity
    var item = new Item
    {
        Name = command.Name
    };

    // Add the item to the current
    // DbContext unit of work
    db.Items.Add(item);

    // This event being returned
    // by the handler will be automatically sent
    // out as a "cascading" message
    return new ItemCreated
    {
        Id = item.Id
    };
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/CreateItemCommandHandler.cs#L5-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handler_using_efcore' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When using the transactional middleware around a message handler, the `DbContext` is used to persist
the outgoing messages as part of Wolverine's outbox support.


## Optimized DbContext Registration

Wolverine can make a few performance optimizations for the `DbContext` integration with Wolverine if you use
this syntax for the service registration:

<!-- snippet: sample_optimized_efcore_registration -->
<a id='snippet-sample_optimized_efcore_registration'></a>
```cs
// If you're okay with this, this will register the DbContext as normally,
// but make some Wolverine specific optimizations at the same time
builder.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(
    x => x.UseSqlServer(connectionString));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L15-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_optimized_efcore_registration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That registration will:

1. Add mappings to your `DbContext` for persisted Wolverine messaging to make the outbox integration a little more efficient by
   allowing Wolverine to utilize the command batching from EF Core for the message storage
2. Sets the `optionsLifetime` to `Singleton` scoped. This allows Wolverine to optimize the construction of your `DbContext`
   objects at runtime when the configuration options do not vary by scope
3. Automatically registers the EF Core support for Wolverine transactional middlware and stateful saga support

## Manually adding Envelope Mapping

If not using the `AddDbContextWithWolverineIntegration()` extension method to register a `DbContext` in your system, you 
can still explicitly add the Wolverine persistent message mapping into your `DbContext` with this call:

<!-- snippet: sample_mapping_envelope_storage_to_dbcontext -->
<a id='snippet-sample_mapping_envelope_storage_to_dbcontext'></a>
```cs
public class SampleMappedDbContext : DbContext
{
    public SampleMappedDbContext(DbContextOptions<SampleMappedDbContext> options) : base(options)
    {
    }

    public DbSet<Item> Items { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // This enables your DbContext to map the incoming and
        // outgoing messages as part of the outbox
        modelBuilder.MapWolverineEnvelopeStorage();
        
        // Your normal EF Core mapping
        modelBuilder.Entity<Item>(map =>
        {
            map.ToTable("items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/EFCore/SampleDbContext.cs#L51-L77' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mapping_envelope_storage_to_dbcontext' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Outbox Outside of Wolverine Handlers

::: tip
In all cases, the `IDbContextOutbox` services expose all the normal `IMessageBus` API.
:::

To use EF Core with the Wolverine outbox outside of a Wolverine message handler, you have a couple options.

First, you can use the `IDbContextOutbox<T>` service where the `T` is your `DbContext` type as shown below:

<!-- snippet: sample_using_dbcontext_outbox_1 -->
<a id='snippet-sample_using_dbcontext_outbox_1'></a>
```cs
[HttpPost("/items/create2")]
public async Task Post(
    [FromBody] CreateItemCommand command,
    [FromServices] IDbContextOutbox<ItemsDbContext> outbox)
{
    // Create a new Item entity
    var item = new Item
    {
        Name = command.Name
    };

    // Add the item to the current
    // DbContext unit of work
    outbox.DbContext.Items.Add(item);

    // Publish a message to take action on the new item
    // in a background thread
    await outbox.PublishAsync(new ItemCreated
    {
        Id = item.Id
    });

    // Commit all changes and flush persisted messages
    // to the persistent outbox
    // in the correct order
    await outbox.SaveChangesAndFlushMessagesAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/CreateItemController.cs#L8-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_dbcontext_outbox_1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or use the `IDbContextOutbox` as shown below, but in this case you will need to explicitly call `Enroll()` on
the `IDbContextOutbox` to connect the outbox sending to the `DbContext`:

<!-- snippet: sample_using_dbcontext_outbox_2 -->
<a id='snippet-sample_using_dbcontext_outbox_2'></a>
```cs
[HttpPost("/items/create3")]
public async Task Post3(
    [FromBody] CreateItemCommand command,
    [FromServices] ItemsDbContext dbContext,
    [FromServices] IDbContextOutbox outbox)
{
    // Create a new Item entity
    var item = new Item
    {
        Name = command.Name
    };

    // Add the item to the current
    // DbContext unit of work
    dbContext.Items.Add(item);

    // Gotta attach the DbContext to the outbox
    // BEFORE sending any messages
    outbox.Enroll(dbContext);
    
    // Publish a message to take action on the new item
    // in a background thread
    await outbox.PublishAsync(new ItemCreated
    {
        Id = item.Id
    });

    // Commit all changes and flush persisted messages
    // to the persistent outbox
    // in the correct order
    await outbox.SaveChangesAndFlushMessagesAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/CreateItemController.cs#L41-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_dbcontext_outbox_2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## As Saga Storage

There's actually nothing to do other than to make a mapping of the `Saga` subclass that's your stateful saga inside
a registered `DbContext`.
