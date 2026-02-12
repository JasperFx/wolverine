# Transactional Middleware

Support for using Wolverine transactional middleware requires an explicit registration on `WolverineOptions`
shown below (it's an extension method):

<!-- snippet: sample_registering_efcore_middleware -->
<a id='snippet-sample_registering_efcore_middleware'></a>
```cs
builder.Host.UseWolverine(opts =>
{
    // Setting up Sql Server-backed message storage
    // This requires a reference to Wolverine.SqlServer
    opts.PersistMessagesWithSqlServer(connectionString, "wolverine");

    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();

    // Enrolling all local queues into the
    // durable inbox/outbox processing
    opts.Policies.UseDurableLocalQueues();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L36-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_efcore_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
When using the opt in `Handlers.AutoApplyTransactions()` option, Wolverine (really Lamar) can detect that your handler method uses a `DbContext` if it's a method argument,
a dependency of any service injected as a method argument, or a dependency of any service injected as a constructor
argument of the handler class.
:::

That will enroll EF Core as both a strategy for stateful saga support and for transactional middleware. With this
option added, Wolverine will wrap transactional middleware around any message handler that has a dependency on any
type of `DbContext` like this one:

<!-- snippet: sample_handler_using_efcore -->
<a id='snippet-sample_handler_using_efcore'></a>
```cs
[Transactional]
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/CreateItemCommandHandler.cs#L7-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handler_using_efcore' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When using the transactional middleware around a message handler, the `DbContext` is used to persist
the outgoing messages as part of Wolverine's outbox support.

## Eager vs Lightweight Transactions <Badge type="tip" text="5.15" />

By default, the EF Core middleware will run in `Eager` mode meaning that Wolverine
will call `DbContext.Database.BeginTransactionAsync()` before your message handler or HTTP
endpoint handler. We do this so that bulk operations can succeed. If all you need to do is
persist entities such that `DbContext.SaveChangesAsync()` gives you all the transactional integrity
you need, you can opt into lightweight transaction code generation instead:

<!-- snippet: sample_using_lightweight_ef_core_transactions -->
<a id='snippet-sample_using_lightweight_ef_core_transactions'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
            x.UseSqlServer(Servers.SqlServerConnectionString));

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
        
        // ONLY use SaveChangesAsync() for transaction boundaries
        // Treat the DbContext as a unit of work, assume there are no
        // bulk operations
        opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);
        opts.Policies.AutoApplyTransactions();

        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType<LightweightModeHandler>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/transaction_middleware_mode_tests.cs#L50-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_lightweight_ef_core_transactions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can also selectively configure the transaction middleware mode on singular message handlers or HTTP endpoints
with the `[Transactional]` attribute like this:

<!-- snippet: sample_explicit_usage_of_transaction_middleware_mode -->
<a id='snippet-sample_explicit_usage_of_transaction_middleware_mode'></a>
```cs
public class LightweightAttributeHandler
{
    [Transactional(Mode = TransactionMiddlewareMode.Lightweight)]
    public static void Handle(LightweightAttributeMessage message, CleanDbContext db)
    {
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/transaction_middleware_mode_tests.cs#L207-L217' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_explicit_usage_of_transaction_middleware_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Auto Apply Transactional Middleware

You can opt into automatically applying the transactional middleware to any handler that depends on a `DbContext` type
with the `AutoApplyTransactions()` option as shown below:

<!-- snippet: sample_bootstrapping_with_auto_apply_transactions_for_sql_server -->
<a id='snippet-sample_bootstrapping_with_auto_apply_transactions_for_sql_server'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("database");

    opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(x =>
    {
        x.UseSqlServer(connectionString);
    });

    // Add the auto transaction middleware attachment policy
    opts.Policies.AutoApplyTransactions();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/SampleUsageWithAutoApplyTransactions.cs#L16-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_auto_apply_transactions_for_sql_server' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With this option, you will no longer need to decorate handler methods with the `[Transactional]` attribute.

## Transaction Middleware Mode

By default, the EF Core transactional middleware uses `TransactionMiddlewareMode.Eager`, which eagerly opens an
explicit database transaction via `Database.BeginTransactionAsync()` before the handler executes. This is appropriate
when you need explicit transaction control, such as when using EF Core bulk operations.

If you prefer to rely solely on `DbContext.SaveChangesAsync()` as your transactional boundary without opening an
explicit database transaction, you can use `TransactionMiddlewareMode.Lightweight`:

```cs
builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithSqlServer(connectionString, "wolverine");

    // Use Lightweight mode — no explicit transaction, relies on SaveChangesAsync()
    opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);

    opts.Policies.UseDurableLocalQueues();
});
```

::: tip
`TransactionMiddlewareMode.Lightweight` is **not** supported or necessary for Marten or RavenDb, which have their own
unit of work implementations.
:::

### Per-Handler Override

You can override the global `TransactionMiddlewareMode` for individual handlers using the `[Transactional]` attribute's
`Mode` property:

```cs
// This handler will use an explicit transaction even if the global mode is Lightweight
[Transactional(Mode = TransactionMiddlewareMode.Eager)]
public static ItemCreated Handle(CreateItemCommand command, ItemsDbContext db)
{
    var item = new Item { Name = command.Name };
    db.Items.Add(item);
    return new ItemCreated { Id = item.Id };
}

// This handler skips the explicit transaction even if the global mode is Eager
[Transactional(Mode = TransactionMiddlewareMode.Lightweight)]
public static void Handle(UpdateItemCommand command, ItemsDbContext db)
{
    // Just uses SaveChangesAsync() without an explicit transaction
}
```

