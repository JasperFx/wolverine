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
    opts.PersistMessagesWithSqlite(connectionString!);

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L15-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setup_sqlite_storage' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L48-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_connection_string_examples' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
In-memory SQLite connection strings are intentionally not supported for Wolverine durability.
Use file-backed SQLite databases instead.
:::

## Connecting with Aspire or a Named Connection String <Badge type="tip" text="6.52" />

Let's say you're running your system under [.NET Aspire](/guide/aspire), and your AppHost declares a SQLite
database and references it from your Wolverine service:

```csharp
// In the AppHost
var database = builder.AddSqlite("wolverinedb");

builder.AddProject<Projects.MyService>("service")
    .WithReference(database)
    .WaitFor(database);
```

Aspire hands your service the connection string at `ConnectionStrings:wolverinedb`, and all you need to tell Wolverine
is that name. In the sample below, the in memory configuration is just standing in for what Aspire would supply:

<!-- snippet: sample_sqlite_persistence_using_named_connection -->
<a id='snippet-sample_sqlite_persistence_using_named_connection'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
    {
        // Under Aspire, builder.AddSqlite("wolverinedb") (CommunityToolkit.Aspire.Hosting.Sqlite) plus
        // .WithReference(...) in the AppHost supplies this
        ["ConnectionStrings:wolverinedb"] = connectionString
    }))
    .UseWolverine(opts =>
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        // Resolve the "wolverinedb" connection string from IConfiguration
        opts.PersistMessagesWithSqliteUsingNamedConnection("wolverinedb", "named5127");
    })
    .StartAsync(TestContext.Current.CancellationToken);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/named_connection_5127.cs#L56-L74' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_persistence_using_named_connection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's no need to pull the connection string out of `IConfiguration` yourself. Wolverine resolves it once the
application's container exists, but before the message store is built. If the name is missing, Wolverine fails at
startup with a single error that lists every missing connection string the application asked for.

::: tip
`AddSqlite()` comes from the CommunityToolkit.Aspire.Hosting.Sqlite package. Whatever value ends up at the named
connection string still has to point at a file-based SQLite database, because Wolverine refuses in-memory SQLite
databases for durability no matter how the connection string gets to it.
:::

If you're using [multi-tenancy with a database per tenant](#multi-tenancy), the static tenant databases can be
resolved by name too:

<!-- snippet: sample_sqlite_static_tenants_using_named_connections -->
<a id='snippet-sample_sqlite_static_tenants_using_named_connections'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:wolverinedb"] = mainConnectionString,
        ["ConnectionStrings:tenant1db"] = tenantConnectionString
    }))
    .UseWolverine(opts =>
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        opts.PersistMessagesWithSqliteUsingNamedConnection("wolverinedb", "named5127")
            .RegisterStaticTenantsUsingNamedConnections(tenants =>
            {
                // tenant id, then the name of its connection string
                tenants.Register("tenant1", "tenant1db");
            });
    })
    .StartAsync(TestContext.Current.CancellationToken);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/named_connection_5127.cs#L111-L132' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_static_tenants_using_named_connections' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Aspire Integration](/guide/aspire) for the full picture.

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
    opts.UseSqlitePersistenceAndTransport(connectionString!)

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L61-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_sqlite_transport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The SQLite transport is strictly queue-based at this point. The queues are configured as durable by default, meaning
that they are utilizing the transactional inbox and outbox. The SQLite queues can also be buffered:

<!-- snippet: sample_setting_sqlite_queue_to_buffered -->
<a id='snippet-sample_setting_sqlite_queue_to_buffered'></a>
```cs
opts.ListenToSqliteQueue("sender").BufferedInMemory();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L195-L198' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_sqlite_queue_to_buffered' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L170-L184' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_polling_configuration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can also override the polling interval for a specific queue:

```cs
opts.ListenToSqliteQueue("inbound").PollingInterval(2.Seconds());
```

When not set, the queue falls back to the global `DurabilitySettings.ScheduledJobPollingTime`.

::: info Control queue
Wolverine has an internal control queue (`dbcontrol`) used for internal operations.
This queue is hardcoded to poll every second and should not be changed to ensure the stability of the application.
:::

### Deterministic Queue Ordering

A SQLite queue row's `timestamp` defaults to `datetime('now')`, which only has one-second precision, so
every message sent to a queue within the same second gets the same timestamp. Scheduled messages that are
promoted to the ready queue together also share one timestamp. Ties are the normal case on SQLite, not an
edge case.

By default, the listeners read rows in timestamp order and hand each batch to the handlers in that order,
and scheduled messages are promoted in `execution_time` order. Neither requires a schema change. Rows with
the same timestamp have no defined order, though. To break those ties deterministically, opt in:

```cs
opts.UseSqlitePersistenceAndTransport(connectionString)
    .EnableDeterministicQueueOrdering();
```

The same option is available from `PersistMessagesWithSqlite(connectionString).EnableMessageTransport(x => x.EnableDeterministicQueueOrdering())`.

The tie-breaker is SQLite's implicit `rowid`. The queue tables have a text primary key, so each row
already carries a `rowid`, and a new row always gets a larger `rowid` than every row still in the table.
No column is added. With the option enabled, the listeners order by `timestamp, rowid`, and promotion
inserts due scheduled messages in `execution_time, rowid` order, so promoted messages keep their order
in the ready queue. It applies to both durable and buffered listeners, and to every tenant database.

The schema change is one index on each queue table: `idx_{table}_timestamp` on the ready queue table and
`idx_{table}_execution_time` on the scheduled table. SQLite stores the `rowid` in every index entry, so
these indexes serve the `timestamp, rowid` and `execution_time, rowid` ordering without a sort. They are
created in place on existing tables, without copying rows, and existing messages keep their `rowid`s.

Messages that are resent to a queue, for example when a listener defers one, are written again and go
to the back of the queue. The order is stable within a queue; it does not define a global order across
different queues or different SQLite files.

The option is disabled by default so existing applications do not receive an automatic queue schema
change. With `AutoCreate.None`, apply the Wolverine resource migration before starting with the option
enabled. Configure the option consistently on every application using the same SQLite file; once the
schema has been upgraded, leaving the option disabled keeps the added indexes. Rolling back to a version
that predates this option may drop the indexes again, which loses no messages.

### Prefixing the Queue Tables <Badge type="tip" text="6.50" />

By default, the SQLite transport names the tables for a queue called `orders` `wolverine_queue_orders` and
`wolverine_queue_orders_scheduled`. That's fine right up until you want two logically separate applications,
or two separate sets of queues, to share one SQLite database file and they both happen to use a queue named
`orders`. Since SQLite doesn't have schemas, Wolverine can't separate those tables the way it would with
PostgreSQL or SQL Server. Instead, you can opt into a prefix on the queue table names:

<!-- snippet: sample_sqlite_queue_table_prefix -->
<a id='snippet-sample_sqlite_queue_table_prefix'></a>
```cs
opts.UseSqlitePersistenceAndTransport("Data Source=shared.db")

    // The "orders" queue now lives in reporting_wolverine_queue_orders
    // and reporting_wolverine_queue_orders_scheduled
    .PrefixName("reporting");

opts.ListenToSqliteQueue("orders");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L110-L118' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_queue_table_prefix' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The same option is available from `PersistMessagesWithSqlite(connectionString).EnableMessageTransport(x => x.PrefixName("reporting"))`,
and from `FisherIntegration.PrefixName` if you're using [Fisher](/guide/durability/fisher/#prefixing-native-sqlite-queue-tables).

A few things to know about this:

* **It's completely opt in.** If you never call `PrefixName()`, or you pass SQLite's built-in `"main"`, the
  queue tables keep the exact names they've always had, so existing applications don't lose track of the
  messages already sitting in those tables.
* **It only applies to the queue tables.** To prefix Wolverine's own inbox, outbox and node tables too, use the
  schema name argument to `PersistMessagesWithSqlite()` as described in [Schema Names](#schema-names).
* **Every application on a queue has to agree.** A sender and a listener using different prefixes are talking
  to different tables, and the messages will just sit there.
* **Changing the prefix later points at new, empty tables.** Wolverine does not copy messages from the old
  tables to the new ones, so drain the queues first or move the rows yourself.
* **SQLite still allows only one writer per file.** The prefix separates table names; it doesn't change how
  SQLite handles concurrent writes.

### Resetting in Tests

`RebuildAsync()` / `ClearAllAsync()` on the message store clear envelope storage only — they leave
this transport's queue and scheduled-message tables alone. To wipe both in an integration test
harness, call [`IHost.ClearAllWolverineStorageAsync()`](/guide/testing.html#resetting-all-wolverine-storage-in-tests),
which leaves the queue tables built but empty across every tenant database.

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

SQLite has no user-defined schemas. The only schema names a plain connection knows are `main`, `temp`, and
whatever has been `ATTACH`ed, so there is nothing for a `schema.table` qualifier to point at.

Wolverine therefore treats the schema name as a **table name prefix**. The default is `main`, which prefixes
nothing — the tables are `wolverine_incoming_envelopes`, `wolverine_outgoing_envelopes` and so on:

```csharp
opts.PersistMessagesWithSqlite(connectionString, "reporting");
// => reporting_wolverine_incoming_envelopes, reporting_wolverine_outgoing_envelopes, ...
```

That gives the setting a meaning SQLite can honour: several logically separate Wolverine table sets living in
one database file without colliding. Saga tables take the same prefix. The advisory lock table
(`wolverine_locks`) deliberately does not — lock ids are derived from the schema name, so prefixed table sets
share one lock table and still take distinct rows out of it.

::: warning
Before Wolverine 6.28.1 the schema name was passed through as a real schema qualifier, which meant any value
other than `main` produced SQL against a database that was never attached and failed at runtime with
`SQLite Error 1: 'no such table: <name>.wolverine_incoming_envelopes'`. See
[GH-3943](https://github.com/JasperFx/wolverine/issues/3943).
:::

The SQLite transport's queue tables are named by the transport rather than by the message store, so this
prefix does not apply to them. To prefix the queue tables as well, see
[Prefixing the Queue Tables](#prefixing-the-queue-tables).

`UseSqlitePersistenceAndTransport()` is intentionally connection-string only:

<!-- snippet: sample_sqlite_connection_string_only_transport -->
<a id='snippet-sample_sqlite_connection_string_only_transport'></a>
```cs
opts.UseSqlitePersistenceAndTransport("Data Source=wolverine.db");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L98-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_connection_string_only_transport' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L127-L138' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_static_tenancy' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L147-L156' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_master_table_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For tenant-specific sends, set `DeliveryOptions.TenantId`.

<!-- snippet: sample_sqlite_tenant_specific_send -->
<a id='snippet-sample_sqlite_tenant_specific_send'></a>
```cs
await host.SendAsync(new SampleTenantMessage("hello"), new DeliveryOptions { TenantId = "red" });
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqliteTests/DocumentationSamples.cs#L162-L165' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqlite_tenant_specific_send' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When transport is enabled, each tenant database gets its own durable queue tables and scheduled polling.

### Concurrency

SQLite uses file-level locking, which means only one writer can access the database at a time. For applications
with high write throughput, consider using PostgreSQL or SQL Server instead. However, for moderate workloads and
single-node deployments, SQLite performs well and eliminates the need for external database infrastructure.

### Compatibility

The SQLite persistence is compatible with any platform supported by [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/). The implementation uses the Weasel.Sqlite library for schema management.
