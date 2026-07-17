# Troubleshooting and Leadership Election

::: info
The main reason to care about this topic is to be able to troubleshoot why messages left stranded by a failed node
are not being recovered in a timely manner
:::

For some technical background, the Wolverine transactional inbox today works through a process of [leadership election](https://en.wikipedia.org/wiki/Leader_election), where only one node 
at any one time is the leader. The recovery of messages from dormant nodes that shut down somehow before they could
finish sending their outgoing or processing all their incoming messages is done through a persistent background agent
assigned to one node by the leader node. 

Long story short, if the message recovery isn't happening very quickly, it's likely some kind of issue with the leadership
election failing to start or to fail over from the previous leader dropping off. 

::: tip
There is no harm in deleting rows from this table. It is strictly a log
:::

As of Wolverine 1.10, there is a table in the PostgreSQL or Sql Server backed message storage called `wolverine_node_records`
that just has a record of detected events relevant to the leader election. All of this information is also logged
through the standard .Net `ILogger`, but it might be easier to understand the data in this table. 

Next, check the `wolverine_nodes` and `wolverine_node_assignments` to see where Wolverine thinks all of the running
agents are across the active nodes. The actual leadership agent is `wolverine://leader`, and you can spot the current
leader by the matching row in the `wolverine_node_assignments` table that refers to the "leader" agent. 

If you are frequently stopping and starting a local process -- especially if you are doing that through a debugger -- you
may want to utilize the `Solo` durability mode explained below:

::: tip
Running on PostgreSQL and seeing frequent **"Lost advisory-lock connection"**, **"stepping down from leadership"**, or **"Detected duplicate agent wolverine://leader/"** log lines in a steady-state cluster? The leader election itself is healthy — it's detecting and recovering from server-side session loss correctly — but the underlying database connection is being dropped by something in the network path (managed-PG idle eviction, k8s service mesh, NAT/conntrack, connection pooler in transaction-pooling mode, etc.). See [Connection Stability for Leader Election](postgresql#connection-stability-for-leader-election) for the configuration knobs that fix it.
:::


## Solo Mode

Let's say that you're working on an individual development machine and frequently stopping and starting the application.
You'd ideally like the transactional inbox and outbox processing to kick in fast, but that subsystem has some known hiccups
recovering from exactly the kind of ungraceful process shutdown that happens when developers suddenly kill off the application
running in a debugger. 

To alleviate the issues that developers have had in the past with this mode, Wolverine 1.10 introduced the "Solo" mode
where the system can be optimized to run as if there's never more than one running node:
[..](..%2F..)
<!-- snippet: sample_configuring_the_solo_mode -->
<a id='snippet-sample_configuring_the_solo_mode'></a>
```cs
var builder = Host.CreateApplicationBuilder();

builder.UseWolverine(opts =>
{
    opts.Services.AddMarten("some connection string")

        // This adds quite a bit of middleware for
        // Marten
        .IntegrateWithWolverine();

    // You want this maybe!
    opts.Policies.AutoApplyTransactions();

    if (builder.Environment.IsDevelopment())
    {
        // But wait! Optimize Wolverine for usage as
        // if there would never be more than one node running
        opts.Durability.Mode = DurabilityMode.Solo;
    }
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L53-L79' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_the_solo_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Running your Wolverine application like this means that Wolverine is able to more quickly start the transactional inbox
and outbox at start up time, and also to immediately recover any persisted incoming or outgoing messages from the previous
execution of the service on your local development box.

## Metrics <Badge type="tip" text="3.6" />

::: tip
These metrics can be used to understand when a Wolverine system is distressed when these numbers grow larger
:::

Wolverine emits observable gauge metrics for the size of the persisted inbox, outbox, and scheduled message counts:

1. `wolverine-inbox-count` - number of persisted, `Incoming` envelopes in the durable inbox
2. `wolverine-outbox-count` - number of persisted, `Outgoing` envelopes in the durable outbox
3. `wolverine-scheduled-count` - number of persisted, `Scheduled` envelopes in the durable inbox

In all cases, if you are using some sort of multi-tenancy where envelopes are stored in separate databsases per tenant,
the metric names above will be suffixed with ".[database name]".

You can disable or modify the polling of these metrics by these settings:

<!-- snippet: sample_configuring_persistence_metrics -->
<a id='snippet-sample_configuring_persistence_metrics'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // This does assume that you have *some* kind of message
        // persistence set up
        
        // This is enabled by default, but just showing that
        // you *could* disable it
        opts.Durability.DurabilityMetricsEnabled = true;

        // The default is 5 seconds, but maybe you want it slower
        // because this does have to do a non-trivial query
        opts.Durability.UpdateMetricsPeriod = 10.Seconds();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L203-L219' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_persistence_metrics' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Metrics polling with many tenant databases <Badge type="tip" text="6.18" />

The counts behind these metrics come from polling each message database, which matters at high database
counts: with database-per-tenant multi-tenancy, hundreds of tenant databases means hundreds of queries
every `UpdateMetricsPeriod`.

Two things bound that cost:

1. **Each node only polls the databases it owns.** A database's metrics are gathered by its durability
   agent, and Wolverine's agent distribution assigns that agent to exactly one node. Databases join and
   leave a node's sweep automatically as agents are redistributed.
2. **Each node polls one database at a time.** Rather than a timer per database all firing together, a
   single sweeper per node walks that node's databases sequentially, spreading them across the
   `UpdateMetricsPeriod` window. **At most one metrics query — and one pooled connection for it — is in
   flight per node, regardless of how many databases that node owns** (see
   [GH-3375](https://github.com/JasperFx/wolverine/issues/3375)).

::: tip
Before 6.18 every database ran its own in-phase poller, so the metrics polling itself could become
significant connection pressure at high database counts — hundreds of near-simultaneous queries each
pinning a connection, plus open/close churn if your connection strings use a short
`Connection Idle Lifetime`. If you are on an older version and see that pattern, upgrading is the fix.
:::

Each database is still polled once per `UpdateMetricsPeriod`; the sweeper changes how the queries are
spaced, not how often any one database is sampled. If you want to reduce the cost further:

1. **Disable the durability metrics** entirely with `opts.Durability.DurabilityMetricsEnabled = false`
   if you don't consume the inbox/outbox/scheduled gauges. Nothing else in Wolverine depends on them —
   this only turns off the observability polling, never the durability agents themselves.
2. **Raise `opts.Durability.UpdateMetricsPeriod`** (default: 5 seconds) to something like 1–5 minutes.
   Queue-depth gauges at tenant-database granularity rarely need 5-second resolution, and the polling
   cost scales directly with the frequency. Raising it also widens the window the sweeper spaces a
   node's databases across.

## Scheduled Message Polling <Badge type="tip" text="6.20" />

A durable scheduled message is just a persisted envelope with a future `execution_time`, so something has
to periodically ask each message database "is anything due yet?". That polling happens every
`opts.Durability.ScheduledJobPollingTime` (default: 5 seconds), and **which node does the polling depends
on the database's role**:

| Store | Polled by | Starts |
|-------|-----------|--------|
| Main and ancillary stores | **Every node** | Immediately at startup |
| Tenant databases (database-per-tenant) | **Only the node that owns that database's durability agent** | Once agent assignment completes |

Either way a due message executes exactly once. Where every node polls, the poll takes a per-database lock
first, so only one node does the work; the rest find the lock taken and move on.

### Why tenant databases are polled by only one node

Before 6.20, *every* node polled *every* tenant database. The lock meant the work was only done once, but it
didn't stop the connection: each losing node still opened a connection, started a transaction, failed to
take the lock, and rolled back — every 5 seconds, against every tenant database. With hundreds of tenant
databases that parks a connection per database per node, and adding a node *multiplied* the polling load
rather than dividing it (see [GH-3376](https://github.com/JasperFx/wolverine/issues/3376)).

Tenant scheduled polling now rides the per-database durability agent, which Wolverine's agent distribution
assigns to exactly one node. The tenant polling load is now spread across your nodes instead of duplicated
on each of them, and adding a node divides it.

Main and ancillary stores deliberately keep polling from every node. There are only a handful of them and
every node already holds connections to them anyway — for heartbeats, leader election, and the control
queues — so there is nothing to save, and polling from every node means scheduled messages start flowing
the moment a host boots rather than waiting on leader election.

### What this means for your deployment

* **Connection footprint from scheduled polling scales with the number of databases, not `databases × nodes`.**
* **Tenant scheduled polling pauses briefly during failover.** If a node goes down, its tenant databases
  aren't polled until their durability agents are reassigned to a surviving node. Nothing is lost — the
  messages are still persisted, and execute once the new owner picks them up. This is the same behavior
  the durable inbox/outbox recovery already has. Main and ancillary stores are unaffected.
* **Tenant databases added at runtime** get scheduled polling automatically as soon as their durability
  agent is assigned.
* **`Solo` mode** runs every agent on the single node, so that node polls every database.
* **Hosts with `opts.Durability.DurabilityAgentEnabled = false`** have no agents at all, so they keep
  polling every database from every node regardless of role.

The tenant durability agents are ordinary Wolverine agents, so you can see who owns what the same way you
inspect any other assignment — they use the `wolverinedb://` URI scheme. If tenant scheduled messages seem
late, confirm the database's agent is actually assigned and running somewhere before looking at
`ScheduledJobPollingTime`.

If your scheduled message latency requirements are loose, raising `ScheduledJobPollingTime` is still the
cheapest way to cut the remaining polling cost:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The default is 5 seconds. Raising this trades scheduled message
        // latency for fewer polling queries against every message database.
        opts.Durability.ScheduledJobPollingTime = 1.Minutes();
    }).StartAsync();
```
