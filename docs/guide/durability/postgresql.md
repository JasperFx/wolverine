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
    opts.PersistMessagesWithPostgresql(connectionString!);

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L174-L199' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setup_postgresql_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Connecting with Aspire or a Named Connection String <Badge type="tip" text="6.52" />

Let's say you're running your system under [.NET Aspire](/guide/aspire), and your AppHost declares a PostgreSQL
database and references it from your Wolverine service:

```csharp
// In the AppHost
var database = builder.AddPostgres("server").AddDatabase("wolverinedb");

builder.AddProject<Projects.MyService>("service")
    .WithReference(database)
    .WaitFor(database);
```

Aspire hands your service the connection string at `ConnectionStrings:wolverinedb`, and all you need to tell Wolverine
is that name. In the sample below, the in memory configuration is just standing in for what Aspire would supply:

<!-- snippet: sample_postgresql_persistence_using_named_connection -->
<a id='snippet-sample_postgresql_persistence_using_named_connection'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
    {
        // Under Aspire, builder.AddPostgres("server").AddDatabase("wolverinedb") plus
        // .WithReference(...) in the AppHost supplies this
        ["ConnectionStrings:wolverinedb"] = Servers.PostgresConnectionString
    }))
    .UseWolverine(opts =>
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        // Resolve the "wolverinedb" connection string from IConfiguration
        opts.PersistMessagesWithPostgresqlUsingNamedConnection("wolverinedb", "named5127");
    })
    .UseResourceSetupOnStartup()
    .StartAsync(TestContext.Current.CancellationToken);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/named_connection_5127.cs#L56-L75' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_postgresql_persistence_using_named_connection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's no need to pull the connection string out of `IConfiguration` yourself. Wolverine resolves it once the
application's container exists, but before the message store is built. If the name is missing, Wolverine fails at
startup with a single error that lists every missing connection string the application asked for.

If you're also using Aspire's *client* integration in your service with `builder.AddNpgsqlDataSource("wolverinedb")`, there's already a pooled
`NpgsqlDataSource` in your container, and you can have Wolverine use that very same data source:

<!-- snippet: sample_postgresql_persistence_using_data_source_from_container -->
<a id='snippet-sample_postgresql_persistence_using_data_source_from_container'></a>
```cs
// e.g. registered by Aspire's builder.AddNpgsqlDataSource("wolverinedb")
opts.Services.AddSingleton(dataSource);

opts.PersistMessagesWithPostgresqlUsingDataSourceFromContainer(schemaName: "named5127");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/named_connection_5127.cs#L98-L105' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_postgresql_persistence_using_data_source_from_container' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

By default Wolverine resolves `NpgsqlDataSource` from the container when the message store is built, but you can pass a
function to pick a keyed registration instead, e.g. `s => s.GetRequiredKeyedService<NpgsqlDataSource>("wolverinedb")`. Wolverine does not dispose a data source it gets from
the container.

If you're using [multi-tenancy with a database per tenant](#multi-tenancy), the static tenant databases can be
resolved by name too:

<!-- snippet: sample_postgresql_static_tenants_using_named_connections -->
<a id='snippet-sample_postgresql_static_tenants_using_named_connections'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:wolverinedb"] = Servers.PostgresConnectionString,
        ["ConnectionStrings:tenant1db"] = Servers.PostgresConnectionString
    }))
    .UseWolverine(opts =>
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        opts.PersistMessagesWithPostgresqlUsingNamedConnection("wolverinedb", "named5127")
            .RegisterStaticTenantsUsingNamedConnections(tenants =>
            {
                // tenant id, then the name of its connection string
                tenants.Register("tenant1", "tenant1db");
            });
    })
    .StartAsync(TestContext.Current.CancellationToken);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/named_connection_5127.cs#L125-L146' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_postgresql_static_tenants_using_named_connections' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Aspire Integration](/guide/aspire) for the full picture.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/compliance_using_table_partitioning.cs#L26-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_inbox_partitioning' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Partitioning also weakens the inbox's uniqueness guarantee. The primary key gains the `status` column,
so PostgreSQL only enforces uniqueness *within* a status partition and one message identity can legally
exist as both `Incoming` and `Scheduled` at the same time -- for example when a broker redelivers a
message while the previous attempt is parked as a scheduled retry. When the scheduled message poller
finds such a pair it discards the scheduled copy in favor of the row that supersedes it -- in the
`incoming` partition or in the `handled` one -- and logs a warning naming the envelope. Where the
superseding row is a handled one the pending retry is dropped and never executed, deliberately:
promoting past it would re-execute a message that already completed, into a row that could never be
marked handled because that too is a cross-partition move onto the key the retained row still holds.

Where the superseding row is an incoming one it is executed in the retry's place. The discarded copy carries the retry state of the earlier attempt,
so the surviving row is executed with the attempt count it was stored with rather than the one the
retry had reached, so the retry budget restarts and the message can be attempted more times than the
endpoint's error policy nominally allows. Under `MessageIdentity.IdOnly` the identity is the envelope id
alone, so such a pair is resolved across listening endpoints rather than within one. This is the same
weakened uniqueness that keeps deduplication claims in their own table; see
[Idempotency](./idempotency).

A message retained after handling by `KeepAfterMessageHandling` -- the setting whose purpose is to absorb
a broker redelivery -- sits in the `handled` partition, where the primary key no longer blocks an insert of
the same identity. The inbox insert checks the `handled` partition itself, so a redelivery arriving inside
that retention window is still discarded as a duplicate, with the same `DuplicateIncomingEnvelopeException`
an unpartitioned inbox raises. The one gap left is timing: a redelivery stored at the very moment the first
copy is being marked handled can still slip in beside it and run a second time, which an unpartitioned
inbox would refuse. That copy is cleaned up when it is marked handled in turn.

The `Incoming`/`Scheduled` pair does not raise `DuplicateIncomingEnvelopeException`, which is the only path
to `MaximumBrokerRedeliveries` -- that setting dead-letters a delivery the inbox rejected as a duplicate and
the listener could not settle, rather than bounding duplicate execution, so it does not bound that pair.
:::

## Connection Stability for Leader Election

::: tip
The single most impactful change you can make for cluster stability in cloud or Kubernetes environments is appending `Keepalive=30;Tcp Keepalive=true` to your Wolverine PostgreSQL connection string. Everything else in this section is context for *why*.
:::

Wolverine's [leader election](leadership-and-troubleshooting) holds a **session-level PostgreSQL advisory lock**. That lock identifies the cluster leader; only the node holding it evaluates the assignment grid and dispatches `AssignAgent` / `ReassignAgent` commands. Note that the leader *assigns* the durability agents rather than running them all itself — each message database's durability agent is assigned to exactly one node, which need not be the leader (see [Scheduled Message Polling](leadership-and-troubleshooting#scheduled-message-polling)). Session-level advisory locks live and die with the Postgres backend session — the moment the leader's session is terminated server-side (TCP RST, idle-connection cull, `pg_terminate_backend`, managed-PG failover, Azure Flexible Server maintenance, k8s service-mesh idle timeout, NAT/conntrack eviction, ELB idle close, etc.) the lock is released and another node can legitimately acquire it.

Wolverine pings the lock-holding connection every `Durability.HealthCheckPollingTime` (default 10 seconds) with `SELECT 1` to detect server-side session loss. When the ping fails the current leader emits:

```
Lost advisory-lock connection for database <name>; clearing held lock ids <ids>
Node <NodeNumber> stepping down from leadership: the leadership advisory lock was released server-side
```

A new election runs on the next tick. If the new leader's `EvaluateAssignmentsAsync` cycle observes both the freshly-elected leader and the ex-leader as still claiming the `wolverine://leader/` agent in the assignment grid (transient window between step-down and the ex-leader's row being cleared), the split-brain healer fires:

```
Detected duplicate agent wolverine://leader/ reported running on Node X and Node Y
— sending StopRemoteAgent to the older copy to heal split-brain residue.
```

**Seeing these log lines occasionally is normal.** Seeing them every minute means the connection between your app pods and PostgreSQL is being dropped at sub-`HealthCheckPollingTime` intervals — Wolverine is detecting and recovering from those drops correctly, but the steady state is wrong.

### Configure TCP keepalives on the Wolverine connection string

Linux's default `tcp_keepalive_time = 7200s` (two hours) is longer than any cloud network path will tolerate. Tell Npgsql to set its own keepalive interval:

```csharp
var connectionString = "Host=...;Database=...;Username=...;Password=...;"
                     + "Keepalive=30;Tcp Keepalive=true";

builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithPostgresql(connectionString);
});
```

30 seconds is a good default — short enough to survive every common cloud idle timeout (Azure Flexible Server's 30-min default, AWS NAT Gateway's 350s, k8s service mesh's typical 60–90s) without flooding the network. If you can pair it with server-side `tcp_keepalives_idle = 30` in `postgresql.conf`, even better, but the client-side setting is sufficient on its own.

### Avoid PgBouncer / RDS Proxy in transaction-pooling mode

Session-level advisory locks **require session identity**. Transaction-pooling poolers (PgBouncer's `pool_mode = transaction`, RDS Proxy's transaction mode, etc.) multiplex multiple application connections onto a smaller pool of backend sessions on a per-transaction basis, so the backend session that took the lock isn't the same one that later checks it. This *will* break leader election — usually quietly, with split-brain symptoms.

If you need pooling between Wolverine and PostgreSQL, use **session pooling** (`pool_mode = session`). Or — preferred for Wolverine specifically — bypass the pooler entirely and connect directly to the database. The connection count Wolverine itself uses for advisory locks and durability operations is modest; pooling rarely pays off for the Wolverine ↔ PG hop in particular.

### Match managed-PG idle eviction to `HealthCheckPollingTime`

If you can configure your managed-PG service's idle-connection eviction (`idle_in_transaction_session_timeout`, vendor-specific connection-idle parameters), make sure the eviction window is **longer** than `Durability.HealthCheckPollingTime`. The 10-second default ping easily survives any reasonable idle timeout, but in environments with very aggressive eviction (some Azure Flex / RDS proxy configurations evict at 15–30s) you may need:

```csharp
opts.Durability.HealthCheckPollingTime = 5.Seconds();
```

The trade-off is more `SELECT 1` round-trips per node per minute, which is negligible.

### Kubernetes service mesh and NetworkPolicy

Istio, Linkerd, and other sidecars commonly default to a 60–90 second TCP idle timeout on outbound connections. Bump the idle timeout for the Wolverine ↔ PG hop above the longest legitimate gap between Wolverine database operations. With the keepalive recommendations above the gap is at most ~10 seconds, so any value over a couple of minutes is fine. If you've recently migrated from VMs to k8s and are now seeing leadership churn (very common report), the service mesh idle timeout is almost always the culprit.

### Defensive retry for transient pool poisoning

Heavy connection churn from rapid leadership cycling can occasionally leave the Npgsql pool with a half-disposed connector that surfaces later as `System.ObjectDisposedException: 'System.Threading.ManualResetEventSlim'` from inside `NpgsqlConnector.ResetCancellation`. This is downstream of the connection-stability issue, not a separate bug, and goes away once you've stabilised the leadership churn. Until then, broaden your `OnException` policy to catch it so the durability agent doesn't spam:

```csharp
opts.Policies.OnException<NpgsqlException>(ex => ex.IsTransient)
    .Or<ObjectDisposedException>()           // defensive: stale pool entries during connection churn
    .RetryOnce()
    .Then.RetryWithCooldown(1.Seconds(), 3.Seconds())
    .AndPauseProcessing(10.Seconds());
```

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
            connectionString!,

            // This argument is the database schema for the envelope storage
            // If separate logical services are targeting the same physical database,
            // you should use a separate schema name for each logical application
            // to make basically *everything* run smoother
            "myapp",

            // This schema name is for the actual PostgreSQL queue tables. If using
            // the PostgreSQL transport between two logical applications, make sure
            // to use the same transportSchema!
            transportSchema: "queues")

        // Tell Wolverine to build out all necessary queue or scheduled message
        // tables on demand as needed
        .AutoProvision()

        // Optional: persist a sequence on queue rows so timestamp ties have a stable order.
        .EnableDeterministicQueueOrdering()

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
        .MaximumMessagesToReceive(50)

        // Override how often to poll for new messages when the queue is idle.
        .PollingInterval(1.Seconds());
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/DocumentationSamples.cs#L13-L67' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_postgres_transport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The PostgreSQL transport is strictly queue-based at this point. The queues are configured as durable by default, meaning
that they are utilizing the transactional inbox and outbox. The PostgreSQL queues can also be buffered:

<!-- snippet: sample_setting_postgres_queue_to_buffered -->
<a id='snippet-sample_setting_postgres_queue_to_buffered'></a>
```cs
opts.ListenToPostgresqlQueue("sender").BufferedInMemory();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/Transport/compliance_tests.cs#L61-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_postgres_queue_to_buffered' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using this option just means that the PostgreSQL queues can be used for both sending or receiving with no integration
with the transactional inbox or outbox. This is a little more performant, but less safe as messages could be
lost if held in memory when the application shuts down unexpectedly. 

### Polling
PostgreSQL queue listeners poll their queue tables on a configured interval. You can optionally have Wolverine wake up
idle listeners as soon as a message lands by using PostgreSQL's `LISTEN/NOTIFY` feature, but read the trade offs in
[Queue Notifications with LISTEN/NOTIFY](#queue-notifications-with-listen-notify) before you turn that on.

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
opts.ListenToPostgresqlQueue("inbound").PollingInterval(2.Seconds());
```

When not set, the queue falls back to the global `DurabilitySettings.ScheduledJobPollingTime`.

### Queue Notifications with LISTEN/NOTIFY <Badge type="tip" text="6.50" />

Out of the box, a PostgreSQL queue listener only finds out about new messages when it polls. When a queue is busy, that
doesn't matter much because the listener goes right back for more as soon as it finishes a batch. When a queue is
*idle* though, a new message sits in the table until the next poll, so your delivery latency is basically your polling
interval. You can make the polling interval smaller, but then every listener is hitting the database more often
whether or not there's anything there.

As an alternative, you can opt into PostgreSQL `LISTEN/NOTIFY` wake ups for the queue listeners like this:

<!-- snippet: sample_enabling_postgresql_queue_notifications -->
<a id='snippet-sample_enabling_postgresql_queue_notifications'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("postgres");
    opts.UsePostgresqlPersistenceAndTransport(connectionString!, "myapp", transportSchema: "queues")
        .AutoProvision()

        // Wake up idle queue listeners with PostgreSQL LISTEN/NOTIFY instead of
        // waiting for the next poll. This holds one extra, long-lived database
        // connection per database per running process!
        .EnableQueueNotifications();

    opts.PublishAllMessages().ToPostgresqlQueue("outbound");
    opts.ListenToPostgresqlQueue("inbound");
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/DocumentationSamples.cs#L72-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_postgresql_queue_notifications' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With that turned on, every time Wolverine puts a message into a PostgreSQL queue table -- a direct send, a move from
the outbox, or a scheduled message coming due -- it also issues a `pg_notify()` for that queue. The listeners for that
queue get woken up and immediately poll. The notification is *only* a wake up call. It doesn't carry the message, and
polling still runs on its normal schedule as a safety net, so a lost notification just means that message gets picked
up by the next regular poll like it would have been anyway.

This feature is completely off by default, and honestly, it should stay off unless you know you need it.

#### The Advantages

* **Much lower latency on idle queues.** In local benchmarking with a 750ms polling interval, the median time from
  sending to handling a message on an idle queue dropped from about 650ms to under 3ms.
* **You can poll less often.** Since new messages wake the listeners up anyway, you can afford a longer
  `PollingInterval()` on your queues, which means less chatter against your database when things are quiet.
* **It's safe to turn on and off.** There's no schema change, and polling remains the actual delivery mechanism. If the
  notification connection drops, Wolverine logs a warning, keeps polling, and reconnects in the background.

#### The Downsides

::: warning
The biggest cost of this feature is **database connections**. Wolverine holds one dedicated, long-lived connection
open for `LISTEN` for *every* PostgreSQL database that has active queue listeners, in *every* running process of your
application. That connection is never returned to the pool while the application is running.
:::

Do the math for your own system before you enable this:

* A single application running 4 nodes against one database uses 4 extra connections. That's probably fine.
* A multi-tenanted application with a database per tenant gets one notification connection per tenant database per
  node. 50 tenant databases across 6 nodes is **300 additional connections** that are open all the time, whether or
  not any messages are moving.

Those connections count against PostgreSQL's `max_connections` and against whatever connection limit your managed
PostgreSQL provider puts on you, and that's often a much tighter limit than you'd think on the smaller cloud tiers.
The notification connection is also taken out of the same Npgsql connection pool that Wolverine uses for everything
else, so it permanently reduces the connections available for actual message handling by one. A data source with
`Maximum Pool Size=1` simply cannot work with this feature.

::: tip
The notification connections show up in `pg_stat_activity` with an `application_name` of
`wolverine_qnotify_{transport schema}`, so you can easily count how many you're really using:

```sql
select count(*) from pg_stat_activity where application_name like 'wolverine_qnotify_%';
```
:::

A few more things to be aware of:

* **Sending gets a little slower.** Every message written to a queue table costs an extra `pg_notify()` round trip to
  the database. That was measured at about 8.6% lower send throughput in a local, single sender benchmark. Your
  mileage will vary.
* **NOTIFY doesn't scale forever.** PostgreSQL serializes the commits of every transaction that issues a `NOTIFY`
  across the whole database server with a global lock. With a lot of processes sending messages at a high rate, that
  lock can become a real bottleneck. And for what it's worth, a queue that busy is already being polled constantly, so
  it gets very little benefit from notifications in the first place.
* **Connection poolers.** `LISTEN` requires a dedicated session, so PgBouncer, RDS Proxy, or anything else running in
  *transaction* pooling mode won't deliver notifications. You'll need session pooling (or a direct connection) for
  the notification connection, and that pins a server connection for its whole lifetime.
* **No Npgsql multiplexing.** Wolverine refuses to start a listener with notifications enabled on a data source using
  `Multiplexing=true`, because `LISTEN` can't work on a multiplexed connection.
* **Everybody has to opt in.** Notifications are published by the *sender*. If one application sends to the queue with
  this option turned on and another sends without it, the second application's messages are only found by polling.
  Likewise, anything outside of Wolverine that writes directly into the queue tables needs to call
  `pg_notify('wolverine_queue_wakeup', '{transport schema}.{queue table name}')` itself to get the faster delivery.

#### When Should You Use This?

My advice is to reach for this when you've got a small number of databases, queues that sit idle a lot of the time,
and a real need for low latency on those queues -- think of request/reply style workflows or user facing operations
running through a PostgreSQL queue. If you're running a lot of tenant databases, are tight on database connections,
or your queues are busy all the time anyway, leave this off and tune `PollingInterval()` instead.

### Deterministic Queue Ordering

PostgreSQL's `now()` returns the same timestamp for every row written in one transaction. By default,
the queue listeners sort dequeued rows by timestamp, but messages with the same timestamp can still be
returned in either order. To persist a tie-breaker for those rows, opt into deterministic queue ordering:

```cs
opts.UsePostgresqlPersistenceAndTransport(connectionString)
    .EnableDeterministicQueueOrdering();
```

This adds an identity `queue_order` column to the ready and scheduled queue tables and composite indexes
for timestamp and execution-time ordering. It applies to both durable and buffered listeners and keeps
scheduled messages in sequence when they are promoted to the ready queue. Existing queue rows are retained
and receive distinct sequence values during migration. Their original order cannot be reconstructed when
their timestamps already tie. This gives a stable order within each queue batch; it does not define global
transaction commit order across concurrent senders, consumers, or different queues.

The option is disabled by default so existing applications do not receive an automatic queue schema
change. With `AutoCreate.None`, apply the Wolverine resource migration before starting with the option
enabled. Configure the option consistently on every application using the same queue schema; once the
schema has been upgraded, leaving the option disabled preserves the added columns and indexes. The legacy
single-column indexes remain and the opt-in adds composite indexes, which require additional storage and
write maintenance.

For a safe rollout, deploy the compatible version with this option disabled to every application sharing
the queue schema. Then apply the opt-in schema migration and enable the option on all consumers. With
`AutoCreate.None`, apply the migration explicitly before enabled listeners start. Rolling back to a binary
that predates this change requires suppressing its automatic queue schema migrations, because its older
table model may remove the added column and indexes.

The PostgreSQL queue tables carry a btree index on the dequeue ordering column so that the
`ORDER BY ... LIMIT n FOR UPDATE SKIP LOCKED` pull each poll performs is an ordered index scan rather
than a scan + sort of the whole table. The legacy timestamp index remains in place; opting into
deterministic ties adds a composite index to cover the sequence ordering too.

Unlike the [Sql Server transport's `OptimizeQueueThroughput()`](./sqlserver.html#optimizing-queue-throughput),
there is no clustered-storage opt-in for PostgreSQL: PostgreSQL tables are heaps (there is no clustered
index to align with the dequeue order), so the index above already captures essentially all of the
available benefit. For very high-churn queues the main operational lever is PostgreSQL autovacuum —
busy queue tables accumulate dead tuples from the constant insert/delete cycle, so ensure autovacuum
is keeping up (and consider per-table autovacuum tuning) rather than reaching for a storage-layout change.

::: info Control queue
Wolverine has an internal control queue (`dbcontrol`) used for internal operations.
This queue is hardcoded to poll every second and should not be changed to ensure the stability of the application.
:::

### Global Partitioning <Badge type="tip" text="6.24" />

PostgreSQL queues can be used as the external transport for
[global partitioned messaging](/guide/messaging/partitioning#global-partitioning). This gives you
cluster-wide sequential processing by group id with **no extra infrastructure** — the shards are
just more tables in the database you already have.

Use `UseShardedPostgresqlQueues()` inside a `GlobalPartitioned()` configuration:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePostgresqlPersistenceAndTransport(connectionString)
            .AutoProvision();

        opts.MessagePartitioning.ByMessage<IOrderMessage>(x => x.OrderId.ToString());

        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            // Creates PostgreSQL queues named "orders1" through "orders4"
            // with matching companion local queues for sequential processing
            topology.UseShardedPostgresqlQueues("orders", 4);
            topology.MessagesImplementing<IOrderMessage>();
        });
    }).StartAsync();
```

That creates queues named `orders1` through `orders4` — each backed by its own
`wolverine_queue_orders{n}` / `wolverine_queue_orders{n}_scheduled` table pair — with companion
local queues `global-orders1` through `global-orders4`. Each shard queue is marked exclusive, so
only one node in the cluster listens to it at a time.

If you only want the *publishing* half — sharded queues with no companion local queues, so every
message really does round-trip through the database — use the `MessagePartitioningRules` variant
instead:

```cs
opts.MessagePartitioning.PublishToShardedPostgresqlQueues("orders", 4, topology =>
{
    topology.MessagesImplementing<IOrderMessage>();
    topology.MaxDegreeOfParallelism = PartitionSlots.Five;
});
```

::: info Long base names
PostgreSQL truncates identifiers at `NAMEDATALEN` (63 characters by default). Wolverine runs every
queue table name through Weasel's deterministic shortening helper, so a long sharded base name still
produces distinct, in-range table names for each shard.
:::

::: warning Multi-tenancy
Under [database-per-tenant storage](#multi-tenancy) the shard queue tables are provisioned in
**every** tenant database and slot routing is unchanged — a group id maps to the same slot number
regardless of tenant. Listening is then assigned per `(queue, database)` pair by the sticky listener
agent family rather than per queue.
:::

### Resetting in Tests

`RebuildAsync()` / `ClearAllAsync()` on the message store clear envelope storage only — they leave
this transport's queue and scheduled-message tables alone. To wipe both in an integration test
harness, call [`IHost.ClearAllWolverineStorageAsync()`](/guide/testing.html#resetting-all-wolverine-storage-in-tests),
which leaves the queue tables built but empty across every tenant database.

## NServiceBus Interoperability <Badge type="tip" text="6.0" />

Wolverine can exchange messages with an [NServiceBus](https://particular.net/nservicebus) endpoint that uses the
[PostgreSQL transport](https://docs.particular.net/transports/sql/) by reading and writing the NServiceBus queue
tables directly — the same database-backed interop available for [SQL Server](/guide/durability/sqlserver.html#nservicebus-interoperability).
One table per queue with a JSON `Headers` column and a raw `Body` column; Wolverine's own durable inbox/outbox still
lives in its PostgreSQL message store, only the *queue* tables belong to NServiceBus.

Because NServiceBus normally owns and provisions its own tables, `AutoProvision` is **off by default** for these endpoints.

```cs
using Wolverine.Postgresql.Transport.NServiceBus;

builder.UseWolverine(opts =>
{
    // Wolverine's durable inbox/outbox lives in PostgreSQL
    opts.PersistMessagesWithPostgresql(connectionString, "wolverine");

    // Opt into the NServiceBus PostgreSQL interop transport (autoProvision: true only if you
    // want Wolverine to create the queue tables itself; NServiceBus usually owns them).
    opts.UseNServiceBusPostgresqlInterop(autoProvision: false);

    // Send Wolverine messages to the "nsb" NServiceBus endpoint table
    opts.PublishMessage<OrderPlaced>().ToNServiceBusPostgresqlQueue("nsb");

    // Listen for messages NServiceBus sends to Wolverine's own "wolverine" table,
    // and use it as the reply address Wolverine stamps onto outgoing messages
    opts.ListenToNServiceBusPostgresqlQueue("wolverine").UseForReplies();

    // Let NServiceBus send interface-typed messages that Wolverine binds to concrete types
    opts.Policies.RegisterInteropMessageAssembly(typeof(IOrderContract).Assembly);
});
```

The queue tables are modeled and migrated through Weasel like every other Wolverine transport table, and the destructive
receive uses `FOR UPDATE SKIP LOCKED` ordered by the NServiceBus `seq` column. A few PostgreSQL-specific notes:

* NServiceBus uses lowercase column names; Wolverine's send/receive SQL matches them with unquoted identifiers.
* NServiceBus addresses queues as the schema-qualified `"schema"."table"`; Wolverine maps the reply address back to the
  bare queue name so request/reply works in both directions.

Tenant propagation works exactly as it does for SQL Server: NServiceBus carries the tenant id in a message
header, and Wolverine maps it to and from `Envelope.TenantId` when you add `MapTenantIdToHeader` /
`MapTenantIdFromHeader` to the endpoints. See the
[SQL Server multi-tenancy section](/guide/durability/sqlserver.html#multi-tenancy) for the full explanation and sample.

The [SQL Server NServiceBus interoperability guide](/guide/durability/sqlserver.html#nservicebus-interoperability)
shows the complete bidirectional setup inline — the shared message contracts, the NServiceBus host configuration, and
a handler — all of which applies here apart from swapping the `*SqlServer*` configuration calls for the `*Postgresql*`
ones shown above. See the [interop tutorial](/tutorials/interop) for the bigger picture.

## MassTransit Interoperability <Badge type="tip" text="6.0" />

Wolverine can also interoperate with a [MassTransit](https://masstransit.io) application that uses the
[PostgreSQL SQL transport](https://masstransit.io/documentation/transports/sql). Unlike NServiceBus' documented
table contract, MassTransit's SQL transport is a function-driven, two-table model
(`transport.message` + `transport.message_delivery`) that MassTransit **owns and migrates itself**, so Wolverine
interoperates by calling MassTransit's stored functions — `send_message` to publish, `fetch_messages` to lease, and
`delete_message`/`unlock_message` to ack/nack — rather than reading and writing a table directly. MassTransit must be
running (or have run) to migrate the `transport` schema; Wolverine never provisions it (beyond optionally calling
`create_queue_v2` for a queue it listens to).

```cs
using Wolverine.Postgresql.Transport.MassTransit;

builder.UseWolverine(opts =>
{
    // Wolverine's durable inbox/outbox lives in PostgreSQL
    opts.PersistMessagesWithPostgresql(connectionString, "wolverine");

    // Opt into the MassTransit PostgreSQL interop transport. autoProvision: true makes Wolverine
    // call create_queue_v2 for its own listening queues.
    opts.UseMassTransitPostgresqlInterop(autoProvision: true);

    // Send Wolverine messages to a MassTransit queue
    opts.PublishMessage<OrderPlaced>().ToMassTransitPostgresqlQueue("masstransit");

    // Listen to the queue MassTransit sends to, and use it for replies
    opts.ListenToMassTransitPostgresqlQueue("wolverine").UseForReplies();

    opts.Policies.RegisterInteropMessageAssembly(typeof(IOrderContract).Assembly);
});
```

Wolverine writes the bare message JSON into the `body` column and the envelope fields into the message columns,
emitting the MassTransit `urn:message:{Namespace}:{TypeName}` message type. Incoming messages are leased through
`fetch_messages` and acked/nacked with the `(message_delivery_id, lock_id)` pair MassTransit hands back. Message
types are resolved against the assemblies you register with `RegisterInteropMessageAssembly`.

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
    opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main")!)

        // Add known tenants at bootstrapping time
        .RegisterStaticTenants(tenants =>
        {
            // Add connection strings for the expected tenant ids
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1")!);
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2")!);
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3")!);
        });
    
    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
    {
        builder.UseNpgsql(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
    }, AutoCreate.CreateOrUpdate);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L27-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_static_tenant_registry_with_postgresql' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Since the underlying [Npgsql library](https://www.npgsql.org/) supports the `DbDataSource` concept, and you might need to use this for a variety of reasons, you can also
directly configure `NpgsqlDataSource` objects for each tenant. This one might be a little more involved, but let's start
by saying that you might be using Aspire to configure PostgreSQL and both the main and tenant databases. In this usage,
Aspire will register `NpgsqlDataSource` services as `Singleton` scoped in your IoC container. We can build an `IWolverineExtension`
that utilizes the IoC container to register Wolverine like so:

<!-- snippet: sample_ourfancypostgresqlmultitenancy -->
<a id='snippet-sample_ourfancypostgresqlmultitenancy'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L163-L185' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ourfancypostgresqlmultitenancy' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L151-L159' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_our_fancy_postgresql_multi_tenancy' title='Start of snippet'>anchor</a></sup>
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
    opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("wolverine")!)

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests.MultiTenancy/MultiTenancyDocumentationSamples.cs#L96-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_postgresql_backed_master_table_tenancy' title='Start of snippet'>anchor</a></sup>
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
* Wolverine's transactional middleware is aware of the multi-tenancy and can connect to the correct database based on the `IMessageContext.TenantId`
  or utilize the tenant id detection in Wolverine.HTTP as well
* You can "plug in" a custom implementation of `ITenantSource<string>` to manage tenant id to connection string assignments in whatever way works for your deployed system


## Lightweight Saga Usage <Badge type="tip" text="3.0" />

See the details on [Lightweight Saga Storage](/guide/durability/sagas.html#lightweight-saga-storage) for more information.

## Integration with Marten

The PostgreSQL message persistence and transport is automatically included with the `AddMarten().IntegrateWithWolverine()`
configuration syntax.

### Aligning the migration advisory lock with Marten

Wolverine takes a session-scoped PostgreSQL advisory lock around `MigrateAsync` to serialize schema migrations
across concurrent processes (preventing duplicate `CREATE SCHEMA IF NOT EXISTS` races that surface as
`23505` errors against `pg_namespace_nspname_index`). The default lock id is `4006`. Marten uses `4004`
by default for its own migrations.

When `IntegrateWithWolverine()` is in use, both frameworks target the same schema. To make them serialize
against the *same* advisory lock — useful when many test fixtures or service replicas boot in parallel —
align the two ids:

```csharp
services.AddMarten(opts =>
{
    opts.Connection(connectionString);
    opts.ApplyChangesLockId = 4004; // default
})
.IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithPostgresql(connectionString)
        // Reuse Marten's lock so Marten and Wolverine migrations serialize together
        .OverrideMigrationLockId(4004);
});
```



### Advisory locks and Marten's async-daemon gap detection

Marten's async daemon (9.16.1+) will not skip a stale event-sequence gap while any Postgres session
that *might* have reserved those sequence numbers is still alive — concretely, any session whose open
transaction predates the gap ([marten#4953](https://github.com/JasperFx/marten/issues/4953)). A
session that parks inside an open transaction for the life of the process therefore looks like a
permanent "possible reserver" and can hold the high-water mark — and every async projection — behind a
genuinely dead gap indefinitely.

Wolverine's long-held locks are deliberately shaped to stay out of that candidate set:

- **Leader election and node coordination** hold *session-scoped* advisory locks on a dedicated,
  transaction-free connection. In `pg_stat_activity` those sessions read `state = 'idle'` with a NULL
  `xact_start`, so Marten's liveness probe never counts them.
- Those sessions are tagged `application_name = 'wolverine-advisory-lock:<database>'` so a
  `pg_stat_activity` scan attributes them at a glance.
- The *transaction-scoped* advisory lock used by the scheduled-message poll lives only for the few
  statements of each poll cycle and commits promptly.

Two rules for combined Marten + Wolverine deployments:

1. **Don't rely on `idle_in_transaction_session_timeout` as a dead-gap backstop.** Older guidance
   suggested it; if you have any component holding a transaction-scoped lock in a long-lived open
   transaction, that setting kills the session (and the lock with it). Prefer upgrading Marten to
   9.16.1+ and, where needed, its `SkipStaleGapsDespiteLiveTransactionsAfter` setting.
2. **Never add keepalive queries inside a long-lived open transaction** in your own code. A periodic
   `select 1` bumps `pg_stat_activity.state_change`, which makes the session look active to Marten's
   liveness fence and re-promotes it to candidate reserver — permanently defeating the mitigation. If a
   session must hold an open transaction, it should stay completely silent; better yet, hold long-lived
   exclusivity as a session-scoped lock on a connection with no transaction at all, the way Wolverine's
   `AdvisoryLock` does.
