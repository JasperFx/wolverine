# SQLite Integration

::: info
Wolverine can use the SQLite durability options with any mix of Entity Framework Core
as a higher level persistence framework. SQLite is a great choice for smaller applications,
development/testing scenarios, or single-node deployments where you want durable messaging
without the overhead of a separate database server.
:::

Wolverine supports a SQLite backed message persistence strategy and even a SQLite backed messaging transport
option. To get started, add the `WolverineFx.Sqlite` dependency to your application:

```bash
dotnet add package WolverineFx.Sqlite
```

## Message Persistence

To enable SQLite to serve as Wolverine's [transactional inbox and outbox](./), you just need to use the `WolverineOptions.PersistMessagesWithSqlite()`
extension method as shown below in a sample:

<!-- snippet: sample_setup_sqlite_storage -->
<a id='snippet-sample_setup_sqlite_storage'></a>
```cs
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("sqlite");

builder.Host.UseWolverine(opts =>
{
    // Setting up SQLite-backed message storage
    // This requires a reference to Wolverine.Sqlite
    opts.PersistMessagesWithSqlite(connectionString);

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L15-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setup_sqlite_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Connection String Examples

Use file-based SQLite databases for Wolverine durability:

<!-- snippet: sample_sqlite_connection_string_examples -->
<a id='snippet-sample_sqlite_connection_string_examples'></a>
```cs
// File-based database (recommended)
opts.PersistMessagesWithSqlite("Data Source=wolverine.db");

// File-based database in an application data folder
opts.PersistMessagesWithSqlite("Data Source=./data/wolverine.db");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L49-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_connection_string_examples' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
In-memory SQLite connection strings are intentionally not supported for Wolverine durability.
Use file-backed SQLite databases instead.
:::

## SQLite Messaging Transport

The `WolverineFx.Sqlite` Nuget also contains a simple messaging transport that was mostly meant to be usable for teams
who want asynchronous queueing without introducing more specialized infrastructure. To enable this transport in your code,
use this option which *also* activates SQLite backed message persistence:

<!-- snippet: sample_using_sqlite_transport -->
<a id='snippet-sample_using_sqlite_transport'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("sqlite");
    opts.UseSqlitePersistenceAndTransport(connectionString)

        // Tell Wolverine to build out all necessary queue or scheduled message
        // tables on demand as needed
        .AutoProvision()

        // Optional that may be helpful in testing, but probably bad
        // in production!
        .AutoPurgeOnStartup();

    // Use this extension method to create subscriber rules
    opts.PublishAllMessages().ToSqliteQueue("outbound");

    // Use this to set up queue listeners
    opts.ListenToSqliteQueue("inbound")

        // Optionally specify how many messages to
        // fetch into the listener at any one time
        .MaximumMessagesToReceive(50);
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L63-L93' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_sqlite_transport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The SQLite transport is strictly queue-based at this point. The queues are configured as durable by default, meaning
that they are utilizing the transactional inbox and outbox. The SQLite queues can also be buffered:

<!-- snippet: sample_setting_sqlite_queue_to_buffered -->
<a id='snippet-sample_setting_sqlite_queue_to_buffered'></a>
```cs
opts.ListenToSqliteQueue("sender").BufferedInMemory();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L186-L190' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_sqlite_queue_to_buffered' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using this option just means that the SQLite queues can be used for both sending or receiving with no integration
with the transactional inbox or outbox. This is a little more performant, but less safe as messages could be
lost if held in memory when the application shuts down unexpectedly.

### Polling
Wolverine has a number of internal polling operations, and any SQLite queues will be polled on a configured interval.
The default polling interval is set in the `DurabilitySettings` class and can be configured at runtime as below:

<!-- snippet: sample_sqlite_polling_configuration -->
<a id='snippet-sample_sqlite_polling_configuration'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Health check message queue/dequeue
    opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(10);

    // Node reassignment checks
    opts.Durability.NodeReassignmentPollingTime = TimeSpan.FromSeconds(5);

    // User queue poll frequency
    opts.Durability.ScheduledJobPollingTime = TimeSpan.FromSeconds(5);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L160-L175' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_polling_configuration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info Control queue
Wolverine has an internal control queue (`dbcontrol`) used for internal operations.
This queue is hardcoded to poll every second and should not be changed to ensure the stability of the application.
:::


## Lightweight Saga Usage

See the details on [Lightweight Saga Storage](/guide/durability/sagas.html#lightweight-saga-storage) for more information.

SQLite saga storage uses a `TEXT` column (JSON serialized) for saga state and supports optimistic concurrency with version tracking.

## SQLite-Specific Considerations

### Advisory Locks

SQLite does not have native advisory locks like PostgreSQL. Wolverine uses a table-based locking mechanism
(`wolverine_locks` table) to emulate advisory locks for distributed locking. Locks are acquired by inserting
rows and released by deleting them.

### Data Types

The SQLite persistence uses the following data type mappings:

| Purpose | SQLite Type |
|---------|-------------|
| Message body | `BLOB` |
| Saga state | `TEXT` (JSON) |
| Timestamps | `TEXT` (stored as `datetime('now')` UTC format) |
| GUIDs | `TEXT` |
| IDs (auto-increment) | `INTEGER` |

### Schema Names

SQLite only supports the `main` schema name at this time. Unlike PostgreSQL or SQL Server, SQLite does not have
a traditional schema system for Wolverine queue and envelope tables.

`UseSqlitePersistenceAndTransport()` is intentionally connection-string only:

<!-- snippet: sample_sqlite_connection_string_only_transport -->
<a id='snippet-sample_sqlite_connection_string_only_transport'></a>
```cs
opts.UseSqlitePersistenceAndTransport("Data Source=wolverine.db");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L101-L105' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_connection_string_only_transport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Multi-Tenancy

SQLite multi-tenancy is supported by mapping tenant ids to separate SQLite files (connection strings).
You can do this with static configuration:

<!-- snippet: sample_sqlite_static_tenancy -->
<a id='snippet-sample_sqlite_static_tenancy'></a>
```cs
opts.PersistMessagesWithSqlite("Data Source=main.db")
    .RegisterStaticTenants(tenants =>
    {
        tenants.Register("red", "Data Source=red.db");
        tenants.Register("blue", "Data Source=blue.db");
    })
    .EnableMessageTransport(x => x.AutoProvision());

opts.ListenToSqliteQueue("incoming").UseDurableInbox();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L114-L126' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_static_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or with Wolverine-managed master-table tenancy for dynamic tenant onboarding:

<!-- snippet: sample_sqlite_master_table_tenancy -->
<a id='snippet-sample_sqlite_master_table_tenancy'></a>
```cs
opts.PersistMessagesWithSqlite("Data Source=main.db")
    .UseMasterTableTenancy(seed =>
    {
        seed.Register("red", "Data Source=red.db");
        seed.Register("blue", "Data Source=blue.db");
    })
    .EnableMessageTransport(x => x.AutoProvision());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L135-L145' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_master_table_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For tenant-specific sends, set `DeliveryOptions.TenantId`.

<!-- snippet: sample_sqlite_tenant_specific_send -->
<a id='snippet-sample_sqlite_tenant_specific_send'></a>
```cs
await host.SendAsync(new SampleTenantMessage("hello"), new DeliveryOptions { TenantId = "red" });
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L151-L155' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_tenant_specific_send' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When transport is enabled, each tenant database gets its own durable queue tables and scheduled polling.

### Concurrency

SQLite uses file-level locking, which means only one writer can access the database at a time. For applications
with high write throughput, consider using PostgreSQL or SQL Server instead. However, for moderate workloads and
single-node deployments, SQLite performs well and eliminates the need for external database infrastructure.

### Compatibility

The SQLite persistence is compatible with any platform supported by [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/). The implementation uses the Weasel.Sqlite library for schema management.
