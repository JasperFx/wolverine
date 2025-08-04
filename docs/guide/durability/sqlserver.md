# Sql Server Integration

Wolverine supports a Sql Server backed message persistence strategy and even a Sql Server backed messaging transport
option. To get started, add the `WolverineFx.SqlServer` dependency to your application:

```bash
dotnet add package WolverineFx.SqlServer
```

## Message Persistence

To enable Sql Server to serve as Wolverine's [transactional inbox and outbox](./), you just need to use the `WolverineOptions.PersistMessagesWithSqlServer()`
extension method as shown below in a sample (that also uses Entity Framework Core):

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

## Sql Server Messaging Transport

::: info
The Sql Server transport was originally conceived as a way to handle much more volume through the scheduled message
functionality of Wolverine over using local queues backed by the transactional inbox.
:::

The `WolverineFx.SqlServer` Nuget also contains a simple messaging transport that was mostly meant to be usable for teams
who want asynchronous queueing without introducing more specialized infrastructure. To enable this transport in your code,
use this option which *also* activates Sql Server backed message persistence:

<!-- snippet: sample_using_sql_server_transport -->
<a id='snippet-sample_using_sql_server_transport'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("sqlserver");
    opts.UseSqlServerPersistenceAndTransport(connectionString, "myapp")

        // Tell Wolverine to build out all necessary queue or scheduled message
        // tables on demand as needed
        .AutoProvision()

        // Optional that may be helpful in testing, but probably bad
        // in production!
        .AutoPurgeOnStartup();

    // Use this extension method to create subscriber rules
    opts.PublishAllMessages().ToSqlServerQueue("outbound");

    // Use this to set up queue listeners
    opts.ListenToSqlServerQueue("inbound")
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Transport/DocumentationSamples.cs#L12-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_sql_server_transport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The Sql Server transport is strictly queue-based at this point. The queues are configured as durable by default, meaning
that they are utilizing the transactional inbox and outbox. The Sql Server queues can also be buffered:

<!-- snippet: sample_setting_sql_server_queue_to_buffered -->
<a id='snippet-sample_setting_sql_server_queue_to_buffered'></a>
```cs
opts.ListenToSqlServerQueue("sender").BufferedInMemory();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Transport/compliance_tests.cs#L67-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_sql_server_queue_to_buffered' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using this option just means that the Sql Server queues can be used for both sending or receiving with no integration 
with the transactional inbox or outbox. This is a little more performant, but less safe as messages could be
lost if held in memory when the application shuts down unexpectedly. 

If you want to use Sql Server as a queueing mechanism between multiple applications, you'll need:

1. To target the same Sql Server database, even if the two applications target different database schemas
2. Be sure to configure the `transportSchema` of the Sql Server transport to be the same between the two applications

Here's an example from the Wolverine tests. Note the `transportSchema` configuration:

<!-- snippet: sample_sql_server_as_queue_between_two_apps -->
<a id='snippet-sample_sql_server_as_queue_between_two_apps'></a>
```cs
_sender = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseSqlServerPersistenceAndTransport(
                Servers.SqlServerConnectionString, 
                "sender",
                
                // If using Sql Server as a queue between multiple applications,
                // be sure to use the same transportSchema setting
                transportSchema:"transport")
            .AutoProvision()
            .AutoPurgeOnStartup();

        opts.PublishMessage<SqlServerFoo>().ToSqlServerQueue("foobar");
        opts.PublishMessage<SqlServerBar>().ToSqlServerQueue("foobar");
        opts.Policies.DisableConventionalLocalRouting();
        opts.Discovery.DisableConventionalDiscovery();

    }).StartAsync();
_listener = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, 
                "listener",
                
                transportSchema:"transport")
            .AutoProvision()
            .AutoPurgeOnStartup();
        opts.PublishMessage<SqlServerBar>().ToSqlServerQueue("foobar");
        opts.ListenToSqlServerQueue("foobar");
        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType<FooBarHandler>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Transport/with_multiple_hosts.cs#L21-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sql_server_as_queue_between_two_apps' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Lightweight Saga Usage <Badge type="tip" text="3.0" />

See the details on [Lightweight Saga Storage](/guide/durability/sagas.html#lightweight-saga-storage) for more information.

## Multi-Tenancy <Badge type="tip" text="4.0" />

You can utilize multi-tenancy through separate databases for each tenant with SQL Server and Wolverine. If utilizing the SQL Server transport 
with multi-tenancy through separate databases per tenant, the SQL Server
queues will be built and monitored for each tenant database as well as any main, non-tenanted database. Also, Wolverine is able to utilize
completely different message storage for its transactional inbox and outbox for each unique database including any main database.
Wolverine is able to activate additional durability agents for itself for any tenant databases added at runtime for tenancy modes
that support dynamic discovery.

To utilize Wolverine managed multi-tenancy, you have a couple main options. The simplest is just using a static configured
set of tenant id to database connections like so:

<!-- snippet: sample_static_tenant_registry_with_sqlserver -->
<a id='snippet-sample_static_tenant_registry_with_sqlserver'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;

builder.UseWolverine(opts =>
{
    // First, you do have to have a "main" PostgreSQL database for messaging persistence
    // that will store information about running nodes, agents, and non-tenanted operations
    opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("main"))

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1"));
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2"));
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3"));
        });
    
    // Just to show that you *can* use more than one DbContext
    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
    {
        // You might have to set the migration assembly
        builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
    }, AutoCreate.CreateOrUpdate);

    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<OrdersDbContext>((builder, connectionString, _) =>
    {
        builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
    }, AutoCreate.CreateOrUpdate);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L56-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_sqlserver' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Wolverine is not yet able to dynamically tear down tenants yet. That's long planned, and honestly probably only happens
when an outside company sponsors that work.
:::

If you need to be able to add new tenants at runtime or just have more tenants than is comfortable living in static configuration
or plenty of other reasons I could think of, you can also use Wolverine's "master table tenancy" approach where tenant id
to database connection string information is kept in a separate database table.

Here's a possible usage of that model:

<!-- snippet: sample_using_sqlserver_backed_master_table_tenancy -->
<a id='snippet-sample_using_sqlserver_backed_master_table_tenancy'></a>
```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;
builder.UseWolverine(opts =>
{
    // You need a main database no matter what that will hold information about the Wolverine system itself
    // and..
    opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("wolverine"))

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/MultiTenancy/MultiTenancyDocumentationSamples.cs#L124-L147' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_sqlserver_backed_master_table_tenancy' title='Start of snippet'>anchor</a></sup>
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












