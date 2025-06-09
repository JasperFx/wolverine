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

