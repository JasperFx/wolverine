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

## Multi-Tenancy

If utilizing the PostgreSQL transport with Marten multi-tenancy through separate databases per tenant, the PostgreSQL
queues will be built an monitored for each tenant database as well as any master database.

## Lightweight Saga Usage <Badge type="tip" text="3.0" />

See the details on [Lightweight Saga Storage](/guide/durability/sagas.html#lightweight-saga-storage) for more information.

## Integration with Marten

The PostgreSQL message persistence and transport is automatically included with the `AddMarten().IntegrateWithWolverine()`
configuration syntax.


