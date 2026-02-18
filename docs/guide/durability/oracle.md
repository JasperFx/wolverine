# Oracle Integration

::: info
Wolverine can use the Oracle durability options with any mix of Entity Framework Core
as a higher level persistence framework
:::

Wolverine supports an Oracle backed message persistence strategy and even an Oracle backed messaging transport
option. To get started, add the `WolverineFx.Oracle` dependency to your application:

```bash
dotnet add package WolverineFx.Oracle
```

## Message Persistence

To enable Oracle to serve as Wolverine's [transactional inbox and outbox](./), you just need to use the `WolverineOptions.PersistMessagesWithOracle()`
extension method as shown below in a sample:

```cs
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("oracle");

builder.Host.UseWolverine(opts =>
{
    // Setting up Oracle-backed message storage
    // This requires a reference to Wolverine.Oracle
    opts.PersistMessagesWithOracle(connectionString);

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

## Oracle Messaging Transport

::: info
All Oracle queues are built into a *WOLVERINE_QUEUES* schema by default.
:::

The `WolverineFx.Oracle` Nuget also contains a simple messaging transport that was mostly meant to be usable for teams
who want asynchronous queueing without introducing more specialized infrastructure. To enable this transport in your code,
use the `EnableMessageTransport()` option which also requires Oracle backed message persistence:

```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("oracle");
    opts.PersistMessagesWithOracle(
            connectionString,

            // This argument is the database schema for the envelope storage
            // If separate logical services are targeting the same physical database,
            // you should use a separate schema name for each logical application
            // to make basically *everything* run smoother
            "MYAPP")

        // Enable the Oracle messaging transport
        .EnableMessageTransport(transport =>
        {
            // Configure the schema name for transport queue tables
            transport.TransportSchemaName("QUEUES");

            // Tell Wolverine to build out all necessary queue or scheduled message
            // tables on demand as needed
            transport.AutoProvision();

            // Optional that may be helpful in testing, but probably bad
            // in production!
            transport.AutoPurgeOnStartup();
        });

    // Use this extension method to create subscriber rules
    opts.PublishAllMessages().ToOracleQueue("outbound");

    // Use this to set up queue listeners
    opts.ListenToOracleQueue("inbound")

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

The Oracle transport is strictly queue-based at this point. The queues are configured as durable by default, meaning
that they are utilizing the transactional inbox and outbox. The Oracle queues can also be buffered:

```cs
opts.ListenToOracleQueue("sender").BufferedInMemory();
```

Using this option just means that the Oracle queues can be used for both sending or receiving with no integration
with the transactional inbox or outbox. This is a little more performant, but less safe as messages could be
lost if held in memory when the application shuts down unexpectedly.

### Polling
Wolverine has a number of internal polling operations, and any Oracle queues will be polled on a configured interval.
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

As of Wolverine 5.x, you can use multi-tenancy through separate databases per tenant with Oracle.

To utilize Wolverine managed multi-tenancy, you have a couple main options. The simplest is just using a static configured
set of tenant id to database connections like so:

```cs
var builder = Host.CreateApplicationBuilder();

var configuration = builder.Configuration;

builder.UseWolverine(opts =>
{
    // First, you do have to have a "main" Oracle database for messaging persistence
    // that will store information about running nodes, agents, and non-tenanted operations
    opts.PersistMessagesWithOracle(configuration.GetConnectionString("main"))

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1"));
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2"));
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3"));
        });
});
```

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
    opts.PersistMessagesWithOracle(configuration.GetConnectionString("wolverine"))

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
* The lightweight saga support for Oracle absolutely works with this model of multi-tenancy
* Wolverine is able to manage all of its database tables including the tenant table itself (`wolverine_tenants`) across both the
  main database and all the tenant databases including schema migrations
* Wolverine's transactional middleware is aware of the multi-tenancy and can connect to the correct database based on the `IMessageContext.TenantId`
  or utilize the tenant id detection in Wolverine.HTTP as well
* You can "plug in" a custom implementation of `ITenantSource<string>` to manage tenant id to connection string assignments in whatever way works for your deployed system

::: warning
Wolverine is not able to dynamically tear down tenants yet. That's long planned, and honestly probably only happens
when an outside company sponsors that work.
:::

## Lightweight Saga Usage

See the details on [Lightweight Saga Storage](/guide/durability/sagas.html#lightweight-saga-storage) for more information.

## Oracle-Specific Considerations

### Schema Names

Oracle schema names are always stored in upper case by Wolverine. The default schema name for envelope storage is `WOLVERINE`,
and the default schema name for transport queues is `WOLVERINE_QUEUES`.

### Advisory Locks

Wolverine uses Oracle's `DBMS_LOCK` package for distributed locking to coordinate scheduled message processing across
nodes. Lock names are derived from a deterministic hash of the schema name.

### Data Types

The Oracle persistence uses the following data type mappings:

| Purpose | Oracle Type |
|---------|------------|
| Message body | `BLOB` |
| GUIDs | `RAW(16)` |
| Timestamps | `TIMESTAMP WITH TIME ZONE` |
| String identifiers | `NVARCHAR2` |

### Compatibility

The Oracle persistence requires:
- Oracle Database 19c+
- Uses the [Oracle.ManagedDataAccess.Core](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core) driver via Weasel.Oracle
