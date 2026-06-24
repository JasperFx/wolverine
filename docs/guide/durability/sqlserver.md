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
    opts.PersistMessagesWithSqlServer(connectionString!, "wolverine");

    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();

    // Enrolling all local queues into the
    // durable inbox/outbox processing
    opts.Policies.UseDurableLocalQueues();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L50-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_efcore_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Aspire Integration

The recommended way to integrate Wolverine with .NET Aspire for SQL Server is to read the connection string injected by Aspire via `IConfiguration.GetConnectionString()`.

**AppHost** (`Aspire.Hosting.SqlServer` NuGet):
```csharp
var sqlserver = builder.AddSqlServer("sqlserver")
    .AddDatabase("wolverine");

builder.AddProject<Projects.MyWorker>("worker")
    .WithReference(sqlserver)
    .WaitFor(sqlserver);
```

**Service project:**
```csharp
var builder = Host.CreateApplicationBuilder(args);

// Aspire injects ConnectionStrings__wolverine automatically via WithReference()
var connectionString = builder.Configuration.GetConnectionString("wolverine")!;

builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithSqlServer(connectionString);
    opts.Policies.UseDurableLocalQueues();
});

await builder.Build().RunAsync();
```

`WaitFor(sqlserver)` in the AppHost ensures SQL Server is healthy before your service starts, so Wolverine's schema setup runs against an available database.

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
    opts.UseSqlServerPersistenceAndTransport(connectionString!, "myapp")

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
        .MaximumMessagesToReceive(50)

        // Override how often to poll for new messages when the queue is idle.
        .PollingInterval(1.Seconds());
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Transport/DocumentationSamples.cs#L13-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_sql_server_transport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The Sql Server transport is strictly queue-based at this point. The queues are configured as durable by default, meaning
that they are utilizing the transactional inbox and outbox. The Sql Server queues can also be buffered:

<!-- snippet: sample_setting_sql_server_queue_to_buffered -->
<a id='snippet-sample_setting_sql_server_queue_to_buffered'></a>
```cs
opts.ListenToSqlServerQueue("sender").BufferedInMemory();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Transport/compliance_tests.cs#L67-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_sql_server_queue_to_buffered' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using this option just means that the Sql Server queues can be used for both sending or receiving with no integration
with the transactional inbox or outbox. This is a little more performant, but less safe as messages could be
lost if held in memory when the application shuts down unexpectedly.

### Polling
The Sql Server transport polls queues on a configured interval. The default interval is controlled globally by
`DurabilitySettings.ScheduledJobPollingTime` (default: 5 seconds).

You can override the polling interval for a specific queue:

```cs
opts.ListenToSqlServerQueue("inbound").PollingInterval(2.Seconds());
```

When not set, the queue falls back to the global `DurabilitySettings.ScheduledJobPollingTime`.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Transport/with_multiple_hosts.cs#L21-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sql_server_as_queue_between_two_apps' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## NServiceBus Interoperability <Badge type="tip" text="6.0" />

Wolverine can exchange messages with an [NServiceBus](https://particular.net/nservicebus) endpoint that uses the
[SQL Server transport](https://docs.particular.net/transports/sql/) by reading and writing the NServiceBus queue
tables directly. This is Particular's documented
[native integration](https://docs.particular.net/transports/sql/native-integration) contract: one table per queue,
a JSON `Headers` column, and a raw `Body` column.

The transport reuses Wolverine's own SQL Server message store for the durable inbox/outbox; only the *queue* tables
belong to NServiceBus. Because NServiceBus normally owns and provisions its own tables, `AutoProvision` is **off by
default** for these endpoints.

Start with the message contracts shared by both applications. In a real system these usually live in a
small contracts assembly referenced by both the Wolverine and NServiceBus hosts:

```cs
// Shared between the Wolverine and NServiceBus applications.
public interface IOrderContract
{
    Guid Id { get; set; }
}

public class OrderPlaced : IOrderContract
{
    public Guid Id { get; set; }
}

public class OrderConfirmed
{
    public Guid Id { get; set; }
}
```

Configure the Wolverine application to read and write the NServiceBus queue tables:

```cs
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport.NServiceBus;

builder.UseWolverine(opts =>
{
    // Wolverine's durable inbox/outbox lives in SQL Server
    opts.PersistMessagesWithSqlServer(connectionString, "wolverine");

    // Opt into the NServiceBus SQL Server interop transport. Pass autoProvision: true only
    // if you want Wolverine to create the queue tables itself (NServiceBus usually owns them).
    opts.UseNServiceBusSqlServerInterop(autoProvision: false);

    // Send Wolverine messages to the "nsb" NServiceBus endpoint table
    opts.PublishMessage<OrderPlaced>().ToNServiceBusSqlServerQueue("nsb");

    // Listen for messages NServiceBus sends to Wolverine's own "wolverine" table,
    // and use it as the reply address Wolverine stamps onto outgoing messages
    opts.ListenToNServiceBusSqlServerQueue("wolverine").UseForReplies();

    // Let NServiceBus send interface-typed messages that Wolverine binds to concrete types
    opts.Policies.RegisterInteropMessageAssembly(typeof(IOrderContract).Assembly);
});
```

The NServiceBus application is configured normally with its own SQL Server transport pointed at the same
database. NServiceBus owns and provisions the queue tables; Wolverine just reads and writes them:

```cs
using NServiceBus;

var endpointConfiguration = new EndpointConfiguration("nsb");
endpointConfiguration.UseSerialization<NewtonsoftJsonSerializer>();

var transport = new SqlServerTransport(connectionString)
{
    TransportTransactionMode = TransportTransactionMode.SendsAtomicWithReceive
};
endpointConfiguration.UseTransport(transport);

// NServiceBus creates its queue tables on startup
endpointConfiguration.EnableInstallers();
```

An NServiceBus handler receives the Wolverine-produced message like any other NServiceBus message and can
reply straight back to Wolverine's listening table:

```cs
public class OrderPlacedHandler : IHandleMessages<OrderPlaced>
{
    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        return context.Reply(new OrderConfirmed { Id = message.Id });
    }
}
```

Wolverine translates between its `Envelope` and the NServiceBus wire format with the standard NServiceBus headers
(`NServiceBus.EnclosedMessageTypes`, `MessageId`, `ConversationId`, `CorrelationId`, `ReplyToAddress`, `ContentType`,
and `TimeSent`). Message-type identity is mapped two ways: outgoing messages carry their concrete type plus their
implemented interfaces so an NServiceBus handler registered against a shared interface still binds, and incoming
`EnclosedMessageTypes` are resolved against the assemblies you register with `RegisterInteropMessageAssembly`.

::: tip
The `Body` written by NServiceBus' JSON serializer begins with a UTF-8 byte order mark. Wolverine strips it on
receive so the payload deserializes cleanly with either the default `System.Text.Json` or a Newtonsoft serializer.
:::

### Multi-Tenancy

NServiceBus implements multi-tenancy as a *persistence* concern rather than a transport one: the SQL Server
transport queue tables live in a single shared database, and the tenant identity travels as a user-defined
**message header** (the Particular [SQL persistence multi-tenancy sample](https://docs.particular.net/persistence/sql/multi-tenant)
uses `tenant_id`). A receiving NServiceBus endpoint reads that header in its `MultiTenantConnectionBuilder`
to open the correct tenant database.

Wolverine maps that header to and from its own `Envelope.TenantId` so the two systems stay tenant-aware across
the boundary. Opt in per endpoint with `MapTenantIdToHeader` on the sending side and `MapTenantIdFromHeader`
on the listening side, passing the header name your NServiceBus endpoint is configured with (defaults to
`tenant_id`):

```cs
builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
    opts.UseNServiceBusSqlServerInterop();

    // Stamp Wolverine's Envelope.TenantId onto the NServiceBus "tenant_id" header
    opts.PublishMessage<OrderPlaced>().ToNServiceBusSqlServerQueue("nsb")
        .MapTenantIdToHeader();

    // Surface the NServiceBus "tenant_id" header back as Envelope.TenantId
    opts.ListenToNServiceBusSqlServerQueue("wolverine")
        .MapTenantIdFromHeader()
        .UseForReplies();
});
```

Now when Wolverine sends with a tenant id (for example `bus.PublishAsync(message, new DeliveryOptions { TenantId = "tenant-green" })`),
the NServiceBus endpoint receives the `tenant_id` header and resolves the matching tenant database; and any
message NServiceBus sends with that header arrives at Wolverine with `Envelope.TenantId` already populated.
The default (non-)tenant is never written as a header, so single-tenant traffic is unaffected.

A full, runnable bidirectional example (both frameworks hosted side by side, including the multi-tenant case)
is maintained in Wolverine's interop test suite. See the [interop tutorial](/tutorials/interop) for the bigger picture.

## Lightweight Saga Usage <Badge type="tip" text="3.0" />

See the details on [Lightweight Saga Storage](/guide/durability/sagas.html#lightweight-saga-storage) for more information.

If you are using string-identified sagas with the lightweight storage, be aware that the default `varchar(100)`
identity column can cause performance issues due to SQL Server's implicit `varchar`/`nvarchar` conversion on
query parameters. See [SQL Server String Identity and nvarchar](/guide/durability/sagas.html#sql-server-string-identity-and-nvarchar)
for the opt-in fix.

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
    opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("main")!)

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1")!);
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2")!);
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3")!);
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L55-L88' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_sqlserver' title='Start of snippet'>anchor</a></sup>
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
    opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("wolverine")!)

        // ...also a table holding the tenant id to connection string information
        .UseMasterTableTenancy(seed =>
        {
            // These registrations are 100% just to seed data for local development
            // Maybe you want to omit this during production?
            // Or do something programmatic by looping through data in the IConfiguration?
            seed.Register("tenant1", configuration.GetConnectionString("tenant1")!);
            seed.Register("tenant2", configuration.GetConnectionString("tenant2")!);
            seed.Register("tenant3", configuration.GetConnectionString("tenant3")!);
        });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L121-L143' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_sqlserver_backed_master_table_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
Wolverine's "master table tenancy" model was unsurprisingly based on Marten's [Master Table Tenancy](https://martendb.io/configuration/multitenancy.html#master-table-tenancy-model) feature
and even shares a little bit of supporting code now.
:::

Here's some more important background on the multi-tenancy support:

* Wolverine is spinning up a completely separate "durability agent" across the application to recover stranded messages in
  the transactional inbox and outbox, and that's done automatically for you
* The lightweight saga support for Sql Server absolutely works with this model of multi-tenancy
* Wolverine is able to manage all of its database tables including the tenant table itself (`wolverine_tenants`) across both the
  main database and all the tenant databases including schema migrations
* Wolverine's transactional middleware is aware of the multi-tenancy and can connect to the correct database based on the `IMessageContext.TenantId`
  or utilize the tenant id detection in Wolverine.HTTP as well
* You can "plug in" a custom implementation of `ITenantSource<string>` to manage tenant id to connection string assignments in whatever way works for your deployed system












