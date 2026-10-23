# MySQL Integration

::: info
Wolverine can use the MySQL durability options with any mix of Entity Framework Core
as a higher level persistence framework
:::

Wolverine supports a MySQL/MariaDB backed message persistence strategy and even a MySQL backed messaging transport
option. To get started, add the `WolverineFx.MySql` dependency to your application:

```bash
dotnet add package WolverineFx.MySql
```

## Message Persistence

To enable MySQL to serve as Wolverine's [transactional inbox and outbox](./), you just need to use the `WolverineOptions.PersistMessagesWithMySql()`
extension method as shown below in a sample:

```cs
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("mysql");

builder.Host.UseWolverine(opts =>
{
    // Setting up MySQL-backed message storage
    // This requires a reference to Wolverine.MySql
    opts.PersistMessagesWithMySql(connectionString);

    // Other Wolverine configuration
});

// This is rebuilding the persistent storage database schema on startup
// and also clearing any persisted envelope state
builder.Host.UseResourceSetupOnStartup();

var app = builder.Build();

// Other ASP.Net Core configuration...

// Using JasperFx opens up command line utilities for managing
// the message storage
return await app.RunJasperFxCommands(args);
```

## Connecting with Aspire or a Named Connection String <Badge type="tip" text="6.52" />

Let's say you're running your system under [.NET Aspire](/guide/aspire), and your AppHost declares a MySQL
database and references it from your Wolverine service:

```csharp
// In the AppHost
var database = builder.AddMySql("server").AddDatabase("wolverinedb");

builder.AddProject<Projects.MyService>("service")
    .WithReference(database)
    .WaitFor(database);
```

Aspire hands your service the connection string at `ConnectionStrings:wolverinedb`, and all you need to tell Wolverine
is that name. In the sample below, the in memory configuration is just standing in for what Aspire would supply:

<!-- snippet: sample_mysql_persistence_using_named_connection -->
<a id='snippet-sample_mysql_persistence_using_named_connection'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
    {
        // Under Aspire, builder.AddMySql("server").AddDatabase("wolverinedb") plus
        // .WithReference(...) in the AppHost supplies this
        ["ConnectionStrings:wolverinedb"] = Servers.MySqlConnectionString
    }))
    .UseWolverine(opts =>
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        // Resolve the "wolverinedb" connection string from IConfiguration
        opts.PersistMessagesWithMySqlUsingNamedConnection("wolverinedb", "named5127");
    })
    .UseResourceSetupOnStartup()
    .StartAsync(TestContext.Current.CancellationToken);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MySql/MySqlTests/named_connection_5127.cs#L56-L75' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mysql_persistence_using_named_connection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's no need to pull the connection string out of `IConfiguration` yourself. Wolverine resolves it once the
application's container exists, but before the message store is built. If the name is missing, Wolverine fails at
startup with a single error that lists every missing connection string the application asked for.

If you're also using Aspire's *client* integration in your service with `builder.AddMySqlDataSource("wolverinedb")`, there's already a pooled
`MySqlDataSource` in your container, and you can have Wolverine use that very same data source:

<!-- snippet: sample_mysql_persistence_using_data_source_from_container -->
<a id='snippet-sample_mysql_persistence_using_data_source_from_container'></a>
```cs
// e.g. registered by Aspire's builder.AddMySqlDataSource("wolverinedb")
opts.Services.AddSingleton(dataSource);

opts.PersistMessagesWithMySqlUsingDataSourceFromContainer(schemaName: "named5127");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MySql/MySqlTests/named_connection_5127.cs#L98-L105' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mysql_persistence_using_data_source_from_container' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

By default Wolverine resolves `MySqlDataSource` from the container when the message store is built, but you can pass a
function to pick a keyed registration instead, e.g. `s => s.GetRequiredKeyedService<MySqlDataSource>("wolverinedb")`. Wolverine does not dispose a data source it gets from
the container.

If you're using [multi-tenancy with a database per tenant](#multi-tenancy), the static tenant databases can be
resolved by name too:

<!-- snippet: sample_mysql_static_tenants_using_named_connections -->
<a id='snippet-sample_mysql_static_tenants_using_named_connections'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:wolverinedb"] = Servers.MySqlConnectionString,
        ["ConnectionStrings:tenant1db"] = Servers.MySqlConnectionString
    }))
    .UseWolverine(opts =>
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        opts.PersistMessagesWithMySqlUsingNamedConnection("wolverinedb", "named5127")
            .RegisterStaticTenantsUsingNamedConnections(tenants =>
            {
                // tenant id, then the name of its connection string
                tenants.Register("tenant1", "tenant1db");
            });
    })
    .StartAsync(TestContext.Current.CancellationToken);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MySql/MySqlTests/named_connection_5127.cs#L125-L146' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mysql_static_tenants_using_named_connections' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Aspire Integration](/guide/aspire) for the full picture.

### Reading the Connection String Yourself

If you'd rather read the connection string that Aspire injects yourself through `IConfiguration.GetConnectionString()`, that works too:

**AppHost** (`Aspire.Hosting.MySql` NuGet):
```csharp
var mysql = builder.AddMySql("mysql")
    .AddDatabase("wolverine");

builder.AddProject<Projects.MyWorker>("worker")
    .WithReference(mysql)
    .WaitFor(mysql);
```

**Service project:**
```csharp
var builder = Host.CreateApplicationBuilder(args);

// Aspire injects ConnectionStrings__wolverine automatically via WithReference()
var connectionString = builder.Configuration.GetConnectionString("wolverine")!;

builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithMySql(connectionString);
    opts.Policies.UseDurableLocalQueues();
});

await builder.Build().RunAsync();
```

`WaitFor(mysql)` in the AppHost ensures MySQL is healthy before your service starts, so Wolverine's schema setup runs against an available database.

## MySQL Messaging Transport

::: info
All MySQL queues are built into a *wolverine_queues* schema at this point.
:::

The `WolverineFx.MySql` Nuget also contains a simple messaging transport that was mostly meant to be usable for teams
who want asynchronous queueing without introducing more specialized infrastructure. To enable this transport in your code,
use this option which *also* activates MySQL backed message persistence:

```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("mysql");
    opts.UseMySqlPersistenceAndTransport(
            connectionString,

            // This argument is the database schema for the envelope storage
            // If separate logical services are targeting the same physical database,
            // you should use a separate schema name for each logical application
            // to make basically *everything* run smoother
            "myapp",

            // This schema name is for the actual MySQL queue tables. If using
            // the MySQL transport between two logical applications, make sure
            // to use the same transportSchema!
            transportSchema:"queues")

        // Tell Wolverine to build out all necessary queue or scheduled message
        // tables on demand as needed
        .AutoProvision()

        // Optional that may be helpful in testing, but probably bad
        // in production!
        .AutoPurgeOnStartup();

    // Use this extension method to create subscriber rules
    opts.PublishAllMessages().ToMySqlQueue("outbound");

    // Use this to set up queue listeners
    opts.ListenToMySqlQueue("inbound")

        .CircuitBreaker(cb =>
        {
            // fine tune the circuit breaker
            // policies here
        })

        // Optionally specify how many messages to
        // fetch into the listener at any one time
        .MaximumMessagesToReceive(50);
});

using var host = builder.Build();
await host.StartAsync();
```

The MySQL transport is strictly queue-based at this point. The queues are configured as durable by default, meaning
that they are utilizing the transactional inbox and outbox. The MySQL queues can also be buffered:

```cs
opts.ListenToMySqlQueue("sender").BufferedInMemory();
```

Using this option just means that the MySQL queues can be used for both sending or receiving with no integration
with the transactional inbox or outbox. This is a little more performant, but less safe as messages could be
lost if held in memory when the application shuts down unexpectedly.

### Polling
Wolverine has a number of internal polling operations, and any MySQL queues will be polled on a configured interval.
The default polling interval is set in the `DurabilitySettings` class and can be configured at runtime as below:

```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Health check message queue/dequeue
    opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(10);

    // Node reassigment checks
    opts.Durability.NodeReassignmentPollingTime = TimeSpan.FromSeconds(5);

    // User queue poll frequency
    opts.Durability.ScheduledJobPollingTime = TimeSpan.FromSeconds(5);
}
```

You can also override the polling interval for a specific queue:

```cs
opts.ListenToMySqlQueue("inbound").PollingInterval(2.Seconds());
```

When not set, the queue falls back to the global `DurabilitySettings.ScheduledJobPollingTime`.

::: info Control queue
Wolverine has an internal control queue (`dbcontrol`) used for internal operations.
This queue is hardcoded to poll every second and should not be changed to ensure the stability of the application.
:::

### Deterministic Queue Ordering

The queue tables store the time a message was enqueued in a `timestamp` column, and the listeners pull and hand over
each batch in `timestamp` order. That column is a plain `DATETIME`, so it only has whole second precision: every message
sent within the same second has the same timestamp, and so does every scheduled message promoted to the ready queue
together. By default, messages with the same timestamp can be returned in any order. To persist a tie-breaker for those
rows, opt into deterministic queue ordering:

```cs
opts.UseMySqlPersistenceAndTransport(connectionString)
    .EnableDeterministicQueueOrdering();
```

or, if you configure the transport through the persistence registration:

```cs
opts.PersistMessagesWithMySql(connectionString)
    .EnableMessageTransport(transport => transport.EnableDeterministicQueueOrdering());
```

This adds a `queue_order BIGINT NOT NULL AUTO_INCREMENT` column to the ready and scheduled queue tables, the unique key
MySQL requires for an `AUTO_INCREMENT` column, and composite indexes on `(timestamp, queue_order)` and
`(execution_time, queue_order)`. Dequeueing then orders by `timestamp, queue_order` for both durable and buffered
listeners, and scheduled messages that come due together are moved to the ready queue in
`execution_time, queue_order` order, so they keep their sequence. On an existing table the column and its unique key are
added in a single `ALTER TABLE`, which keeps the existing rows and gives each of them a distinct value. Their original
order cannot be reconstructed when their timestamps already tie. This gives a stable order within each queue; it does not
define a global commit order across concurrent senders, consumers, or different queues.

The option is disabled by default so existing applications do not receive an automatic queue schema change. With
`AutoCreate.None`, apply the Wolverine resource migration before starting with the option enabled. Configure the option
consistently on every application using the same queue tables; once the schema has been upgraded, leaving the option
disabled preserves the added column and indexes. The new indexes require additional storage and write maintenance, and
the `ALTER TABLE` rebuilds the table, so expect it to take a while on a large backlog.

For a safe rollout, deploy the compatible version with this option disabled to every application sharing the queue
tables. Then apply the opt-in schema migration and enable the option on all consumers. Rolling back to a binary that
predates this change requires suppressing its automatic queue schema migrations, because its older table model may try
to remove the added column and indexes.


### Resetting in Tests

`RebuildAsync()` / `ClearAllAsync()` on the message store clear envelope storage only — they leave
this transport's queue and scheduled-message tables alone. To wipe both in an integration test
harness, call [`IHost.ClearAllWolverineStorageAsync()`](/guide/testing.html#resetting-all-wolverine-storage-in-tests),
which leaves the queue tables built but empty across every tenant database.

## Multi-Tenancy

As of Wolverine 5.x, you can use multi-tenancy through separate databases per tenant with MySQL:

To utilize Wolverine managed multi-tenancy, you have a couple main options. The simplest is just using a static configured
set of tenant id to database connections like so:

```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;

builder.UseWolverine(opts =>
{
    // First, you do have to have a "main" MySQL database for messaging persistence
    // that will store information about running nodes, agents, and non-tenanted operations
    opts.PersistMessagesWithMySql(configuration.GetConnectionString("main"))

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1"));
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2"));
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3"));
        });

    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
    {
        builder.UseMySql(connectionString.Value, ServerVersion.AutoDetect(connectionString.Value),
            b => b.MigrationsAssembly("MultiTenantedEfCoreWithMySql"));
    }, AutoCreate.CreateOrUpdate);
});
```

Since the underlying [MySqlConnector library](https://mysqlconnector.net/) supports the `MySqlDataSource` concept, and you might need to use this for a variety of reasons, you can also
directly configure `MySqlDataSource` objects for each tenant. This one might be a little more involved, but let's start
by saying that you might be using Aspire to configure MySQL and both the main and tenant databases. In this usage,
Aspire will register `MySqlDataSource` services as `Singleton` scoped in your IoC container. We can build an `IWolverineExtension`
that utilizes the IoC container to register Wolverine like so:

```cs
public class OurFancyMySQLMultiTenancy : IWolverineExtension
{
    private readonly IServiceProvider _provider;

    public OurFancyMySQLMultiTenancy(IServiceProvider provider)
    {
        _provider = provider;
    }

    public void Configure(WolverineOptions options)
    {
        options.PersistMessagesWithMySql(_provider.GetRequiredService<MySqlDataSource>())
            .RegisterStaticTenantsByDataSource(tenants =>
            {
                tenants.Register("tenant1", _provider.GetRequiredKeyedService<MySqlDataSource>("tenant1"));
                tenants.Register("tenant2", _provider.GetRequiredKeyedService<MySqlDataSource>("tenant2"));
                tenants.Register("tenant3", _provider.GetRequiredKeyedService<MySqlDataSource>("tenant3"));
            });
    }
}
```

And add that to the greater application like so:

```cs
var host = Host.CreateDefaultBuilder()
    .UseWolverine()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IWolverineExtension, OurFancyMySQLMultiTenancy>();
    }).StartAsync();
```

::: warning
Wolverine is not able to dynamically tear down tenants yet. That's long planned, and honestly probably only happens
when an outside company sponsors that work.
:::

If you need to be able to add new tenants at runtime or just have more tenants than is comfortable living in static configuration
or plenty of other reasons I could think of, you can also use Wolverine's "master table tenancy" approach where tenant id
to database connection string information is kept in a separate database table.

Here's a possible usage of that model:

```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;
builder.UseWolverine(opts =>
{
    // You need a main database no matter what that will hold information about the Wolverine system itself
    // and..
    opts.PersistMessagesWithMySql(configuration.GetConnectionString("wolverine"))

        // ...also a table holding the tenant id to connection string information
        .UseMasterTableTenancy(seed =>
        {
            // These registrations are 100% just to seed data for local development
            // Maybe you want to omit this during production?
            // Or do something programmatic by looping through data in the IConfiguration?
            seed.Register("tenant1", configuration.GetConnectionString("tenant1"));
            seed.Register("tenant2", configuration.GetConnectionString("tenant2"));
            seed.Register("tenant3", configuration.GetConnectionString("tenant3"));
        });

});
```

Here's some more important background on the multi-tenancy support:

* Wolverine is spinning up a completely separate "durability agent" across the application to recover stranded messages in
  the transactional inbox and outbox, and that's done automatically for you
* The lightweight saga support for MySQL absolutely works with this model of multi-tenancy
* Wolverine is able to manage all of its database tables including the tenant table itself (`wolverine_tenants`) across both the
  main database and all the tenant databases including schema migrations
* Wolverine's transactional middleware is aware of the multi-tenancy and can connect to the correct database based on the `IMessageContext.TenantId`
  or utilize the tenant id detection in Wolverine.HTTP as well
* You can "plug in" a custom implementation of `ITenantSource<string>` to manage tenant id to connection string assignments in whatever way works for your deployed system


## Lightweight Saga Usage

See the details on [Lightweight Saga Storage](/guide/durability/sagas.html#lightweight-saga-storage) for more information.

MySQL saga storage uses the native `JSON` column type for saga state and supports optimistic concurrency with version tracking.

## MySQL-Specific Considerations

### Advisory Locks

Wolverine uses MySQL's `GET_LOCK()` and `RELEASE_LOCK()` functions for distributed locking. These locks are session-scoped
and automatically released when the connection is closed. Lock names follow the pattern `wolverine_{lockId}`.

### Data Types

The MySQL persistence uses the following data type mappings:

| Purpose | MySQL Type |
|---------|------------|
| Message body | `LONGBLOB` |
| Saga state | `JSON` |
| Timestamps | `DATETIME` (whole second precision) |
| GUIDs | `CHAR(36)` |

### Compatibility

The MySQL persistence is compatible with:
- MySQL 8.0+
- MariaDB 10.5+

The implementation uses the [MySqlConnector](https://mysqlconnector.net/) driver via Weasel.MySql.
