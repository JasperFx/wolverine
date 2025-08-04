# Transactional Inbox and Outbox with EF Core

Wolverine is able to integrate with EF Core inside of its transactional middleware in either message handlers or HTTP
endpoints to apply the [transactional inbox and outbox mechanics](/guide/durability/) for outgoing messages (local messages actually go straight to the inbox).

::: tip
Database round trips, or really any network round trips, are a frequent cause of poor system performance. Wolverine and other
Critter Stack tools try to take this into account in its internals. With the EF Core integration, you might need to do just a little
bit to help Wolverine out with mapping envelope types to take advantage of database query batching.
:::

You can optimize this by adding mappings for Wolverine's envelope storage to your `DbContext` types such that Wolverine can
just use EF Core to persist new messages and depend on EF Core database command batching. Otherwise Wolverine has to use
the exposed database `DbConnection` off of the active `DbContext` and make completely separate calls to the database (but at least
in the same transaction!) to persist new messages at the same time it's calling `DbContext.SaveChangesAsync()` with any
pending entity changes. 

You can help Wolverine out by either using the manual envelope mapping explained next, or registering your `DbContext`
with the `AddDbContextWithWolverineIntegration<T>()` option that quietly adds the Wolverine envelope storage mapping
to that `DbContext` for you.

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
            map.ToTable("items", "mt_items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/SampleDbContext.cs#L31-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mapping_envelope_storage_to_dbcontext' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_mapping_envelope_storage_to_dbcontext-1'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/SampleDbContext.cs#L56-L82' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mapping_envelope_storage_to_dbcontext-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Outbox Outside of Wolverine Handlers

::: warning
Honestly, we had to do this feature, but it's just always going to be easiest to use Wolverine HTTP handlers or message handlers
for the EF Core + transactional outbox support.
:::

::: tip
In all cases, the `IDbContextOutbox` services expose all the normal `IMessageBus` API.
:::

To use EF Core with the Wolverine outbox outside of a Wolverine message handler (maybe inside an ASP.Net MVC Core `Controller`, or within Minimal API maybe?), you have a couple options.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/CreateItemController.cs#L12-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_dbcontext_outbox_1' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/CreateItemController.cs#L45-L80' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_dbcontext_outbox_2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
