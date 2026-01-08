# PostgreSQL Integration

::: info
Wolverine can happily use the PostgreSQL durability options with any mix of Entity Framework Core and/or
Marten as a higher level persistence framework
:::

Wolverine supports a PostgreSQL backed message persistence strategy and even a PostgreSQL backed messaging transport
option. To get started, add the `WolverineFx.Postgresql` dependency to your application:

```bash
dotnet add package WolverineFx.Postgresql
```

## Message Persistence

To enable PostgreSQL to serve as Wolverine's [transactional inbox and outbox](./), you just need to use the `WolverineOptions.PersistMessagesWithPostgresql()`
extension method as shown below in a sample:

<!-- snippet: sample_setup_postgresql_storage -->
<a id='snippet-sample_setup_postgresql_storage'></a>
```cs
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("postgres");

builder.Host.UseWolverine(opts =>
{
    // Setting up Postgresql-backed message storage
    // This requires a reference to Wolverine.Postgresql
    opts.PersistMessagesWithPostgresql(connectionString);

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L164-L190' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setup_postgresql_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Optimizing the Message Store <Badge type="tip" text="5.3" />

For PostgreSQL, you can enable PostgreSQL backed partitioning for the inbox table
as an optimization. This is not enabled by default just to avoid causing database
migrations in a minor point release. Note that this will have some significant benefits
for inbox/outbox metrics gathering in the future:

<!-- snippet: sample_enabling_inbox_partitioning -->
<a id='snippet-sample_enabling_inbox_partitioning'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Durability.EnableInboxPartitioning = true;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/compliance_using_table_partitioning.cs#L26-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_inbox_partitioning' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## PostgreSQL Messaging Transport <Badge type="tip" text="2.5" />

::: info
All PostgreSQL queues are built into a *wolverine_queues* schema at this point. 
:::

The `WolverineFx.PostgreSQL` Nuget also contains a simple messaging transport that was mostly meant to be usable for teams
who want asynchronous queueing without introducing more specialized infrastructure. To enable this transport in your code,
use this option which *also* activates PostgreSQL backed message persistence:

<!-- snippet: sample_using_postgres_transport -->
<a id='snippet-sample_using_postgres_transport'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("postgres");
    opts.UsePostgresqlPersistenceAndTransport(
            connectionString, 
            
            // This argument is the database schema for the envelope storage
            // If separate logical services are targeting the same physical database,
            // you should use a separate schema name for each logical application
            // to make basically *everything* run smoother
            "myapp", 
            
            // This schema name is for the actual PostgreSQL queue tables. If using
            // the PostgreSQL transport between two logical applications, make sure
            // to use the same transportSchema!
            transportSchema:"queues")

        // Tell Wolverine to build out all necessary queue or scheduled message
        // tables on demand as needed
        .AutoProvision()

        // Optional that may be helpful in testing, but probably bad
        // in production!
        .AutoPurgeOnStartup();

    // Use this extension method to create subscriber rules
    opts.PublishAllMessages().ToPostgresqlQueue("outbound");

    // Use this to set up queue listeners
    opts.ListenToPostgresqlQueue("inbound")

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/DocumentationSamples.cs#L12-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_postgres_transport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The PostgreSQL transport is strictly queue-based at this point. The queues are configured as durable by default, meaning
that they are utilizing the transactional inbox and outbox. The PostgreSQL queues can also be buffered:

<!-- snippet: sample_setting_postgres_queue_to_buffered -->
<a id='snippet-sample_setting_postgres_queue_to_buffered'></a>
```cs
opts.ListenToPostgresqlQueue("sender").BufferedInMemory();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/Transport/compliance_tests.cs#L64-L68' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_postgres_queue_to_buffered' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using this option just means that the PostgreSQL queues can be used for both sending or receiving with no integration
with the transactional inbox or outbox. This is a little more performant, but less safe as messages could be
lost if held in memory when the application shuts down unexpectedly. 

### Polling
Wolverine has a number of internal polling operations, and any PostgreSQL queues will be polled on a configured interval as Wolverine does not use the PostgreSQL `LISTEN/NOTIFY` feature at this time.   
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

::: info Control queue  
Wolverine has an internal control queue (`dbcontrol`) used for internal operations.  
This queue is hardcoded to poll every second and should not be changed to ensure the stability of the application.
:::


## Multi-Tenancy

As of Wolverine 4.0, you have two ways to use multi-tenancy through separate databases per tenant with PostgreSQL:

1. Using [Marten's multi-tenancy support](https://martendb.io/configuration/multitenancy.html) and the `IntegrateWithWolverine()` option
2. Directly configure PostgreSQL databases with Wolverine managed multi-tenancy <Badge type="tip" text="4.0" />

In both cases, if utilizing the PostgreSQL transport with multi-tenancy through separate databases per tenant, the PostgreSQL
queues will be built and monitored for each tenant database as well as any main, non-tenanted database. Also, Wolverine is able to utilize
completely different message storage for its transactional inbox and outbox for each unique database including any main database.
Wolverine is able to activate additional durability agents for itself for any tenant databases added at runtime for tenancy modes
that support dynamic discovery. 

To utilize Wolverine managed multi-tenancy, you have a couple main options. The simplest is just using a static configured
set of tenant id to database connections like so:

<!-- snippet: sample_static_tenant_registry_with_postgresql -->
<a id='snippet-sample_static_tenant_registry_with_postgresql'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;

builder.UseWolverine(opts =>
{
    // First, you do have to have a "main" PostgreSQL database for messaging persistence
    // that will store information about running nodes, agents, and non-tenanted operations
    opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main"))

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
        builder.UseNpgsql(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
    }, AutoCreate.CreateOrUpdate);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L24-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_postgresql' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Since the underlying [Npgsql library](https://www.npgsql.org/) supports the `DbDataSource` concept, and you might need to use this for a variety of reasons, you can also
directly configure `NpgsqlDataSource` objects for each tenant. This one might be a little more involved, but let's start
by saying that you might be using Aspire to configure PostgreSQL and both the main and tenant databases. In this usage,
Aspire will register `NpgsqlDataSource` services as `Singleton` scoped in your IoC container. We can build an `IWolverineExtension`
that utilizes the IoC container to register Wolverine like so:

<!-- snippet: sample_OurFancyPostgreSQLMultiTenancy -->
<a id='snippet-sample_OurFancyPostgreSQLMultiTenancy'></a>
```cs
public class OurFancyPostgreSQLMultiTenancy : IWolverineExtension
{
    private readonly IServiceProvider _provider;

    public OurFancyPostgreSQLMultiTenancy(IServiceProvider provider)
    {
        _provider = provider;
    }

    public void Configure(WolverineOptions options)
    {
        options.PersistMessagesWithPostgresql(_provider.GetRequiredService<NpgsqlDataSource>())
            .RegisterStaticTenantsByDataSource(tenants =>
            {
                tenants.Register("tenant1", _provider.GetRequiredKeyedService<NpgsqlDataSource>("tenant1"));
                tenants.Register("tenant2", _provider.GetRequiredKeyedService<NpgsqlDataSource>("tenant2"));
                tenants.Register("tenant3", _provider.GetRequiredKeyedService<NpgsqlDataSource>("tenant3"));
            });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L165-L188' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_OurFancyPostgreSQLMultiTenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And add that to the greater application like so:

<!-- snippet: sample_adding_our_fancy_postgresql_multi_tenancy -->
<a id='snippet-sample_adding_our_fancy_postgresql_multi_tenancy'></a>
```cs
var host = Host.CreateDefaultBuilder()
    .UseWolverine()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IWolverineExtension, OurFancyPostgreSQLMultiTenancy>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L152-L161' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_our_fancy_postgresql_multi_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Neither Marten nor Wolverine is able to dynamically tear down tenants yet. That's long planned, and honestly probably only happens
when an outside company sponsors that work.
:::

If you need to be able to add new tenants at runtime or just have more tenants than is comfortable living in static configuration
or plenty of other reasons I could think of, you can also use Wolverine's "master table tenancy" approach where tenant id
to database connection string information is kept in a separate database table. 

Here's a possible usage of that model:

<!-- snippet: sample_using_postgresql_backed_master_table_tenancy -->
<a id='snippet-sample_using_postgresql_backed_master_table_tenancy'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;
builder.UseWolverine(opts =>
{
    // You need a main database no matter what that will hold information about the Wolverine system itself
    // and..
    opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("wolverine"))

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L95-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_postgresql_backed_master_table_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
Wolverine's "master table tenancy" model was unsurprisingly based on Marten's [Master Table Tenancy](https://martendb.io/configuration/multitenancy.html#master-table-tenancy-model) feature
and even shares a little bit of supporting code now.
:::

Here's some more important background on the multi-tenancy support:

* Wolverine is spinning up a completely separate "durability agent" across the application to recover stranded messages in
  the transactional inbox and outbox, and that's done automatically for you
* The lightweight saga support for PostgreSQL absolutely works with this model of multi-tenancy
* Wolverine is able to manage all of its database tables including the tenant table itself (`wolverine_tenants`) across both the
  main database and all the tenant databases including schema migrations
* Wolverine's transactional middleware is aware of the multi-tenancy and can connect to the correct database based on the `IMesageContext.TenantId`
  or utilize the tenant id detection in Wolverine.HTTP as well
* You can "plug in" a custom implementation of `ITenantSource<string>` to manage tenant id to connection string assignments in whatever way works for your deployed system


## Lightweight Saga Usage <Badge type="tip" text="3.0" />

See the details on [Lightweight Saga Storage](/guide/durability/sagas.html#lightweight-saga-storage) for more information.

## Integration with Marten

The PostgreSQL message persistence and transport is automatically included with the `AddMarten().IntegrateWithWolverine()`
configuration syntax.


