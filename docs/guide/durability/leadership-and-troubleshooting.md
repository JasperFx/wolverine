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

## Capacity-Aware Agent Assignment <Badge type="tip" text="6.40" />

Let's say you're running a multi-tenanted system with a database per tenant, and Wolverine is spreading a few thousand
Marten async daemon agents across your cluster. Out of the box, the leader divides those agents up evenly by node count
and hopes for the best. That works fine when every agent costs about the same, but honestly, that's very often not the
case -- one node draws the three busiest tenant databases and spends its life in garbage collection while its neighbors
sit there mostly idle.

Capacity-aware assignment lets each node tell the leader how loaded it actually is, and the leader takes that into
account when it decides where agents should run:

<!-- snippet: sample_capacity_aware_assignment -->
<a id='snippet-sample_capacity_aware_assignment'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PersistMessagesWithPostgresql("some connection string");

        // Let the leader take each node's advertised load into account
        // when it decides where agents run
        opts.Durability.CapacityAwareAssignment = true;

        // Required! There's deliberately no default here
        opts.Durability.NodeLoadMonitor = new MemoryPressureLoadMonitor();

        // Optional, these are the defaults
        opts.Durability.NodeOverloadThreshold = 90;
        opts.Durability.OverloadShedBatchSize = 1;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CapacityAwareAssignment.cs#L12-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_capacity_aware_assignment' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Turning this on provisions a new `load_factor` column on the `wolverine_nodes` table. If you're running with
`AutoCreate.None` -- or in a process that doesn't have DDL rights against your database -- apply the schema migration
*before* you flip the flag on. Every statement that names that column is gated on the flag too, so leaving it off
migrates nothing and reads nothing.
:::

### You Have to Supply the Load Monitor

There's deliberately no default `INodeLoadMonitor`. What "load" even means is specific to what your application does --
memory for a node running thousands of daemon shards, queue depth or handler latency for a node doing ordinary message
work -- and any built in default would just be a guess that looks authoritative while being wrong for most deployments.

Starting up with `CapacityAwareAssignment` on and no monitor is a startup exception rather than a quiet fallback, and
that's worth explaining because the failure mode here is nastier than it first looks. A node that advertises *nothing*
is read by the leader as having unlimited headroom. So a missing or broken monitor doesn't make the feature inert on
that node, it makes that node the cluster's favorite place to put new work.

For the memory case there's `MemoryPressureLoadMonitor` in the box, which reports this process's resident memory as a
percentage of the memory limit it's actually running under. A rising reading is taken immediately while a falling one
decays gradually, so one lucky GC can't mask sustained pressure.

::: warning
`MemoryPressureLoadMonitor` needs a memory limit to actually exist. On a bare VM, or a container started without
`--memory`, there's no ceiling to measure against and it returns null rather than inventing a denominator. If you're
running somewhere that the limit is known to your application but not visible to the process, there's a constructor
overload that takes the limit in bytes.
:::

Writing your own is a single method:

<!-- snippet: sample_writing_a_node_load_monitor -->
<a id='snippet-sample_writing_a_node_load_monitor'></a>
```cs
public class QueueDepthLoadMonitor : INodeLoadMonitor
{
    private readonly IWorkTracker _tracker;

    // Do whatever dependency injection you need in your own constructor,
    // just remember that this is a singleton
    public QueueDepthLoadMonitor(IWorkTracker tracker)
    {
        _tracker = tracker;
    }

    public QueueDepthLoadMonitor() : this(new NullWorkTracker())
    {
    }

    public double? CurrentLoad()
    {
        // This is called on every heartbeat, so it needs to be cheap and it
        // absolutely cannot block
        var depth = _tracker.CurrentDepth;

        // Return null if you genuinely have no signal right now. Careful though,
        // the leader reads "no reading" as "this node has headroom" -- so don't
        // use null as a way of saying "lightly loaded"
        if (depth < 0) return null;

        // 0-100, where 100 means "completely full"
        return Math.Clamp(100.0 * depth / _tracker.Capacity, 0, 100);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CapacityAwareAssignment.cs#L51-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_writing_a_node_load_monitor' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And then just hand Wolverine your implementation:

<!-- snippet: sample_custom_node_load_monitor -->
<a id='snippet-sample_custom_node_load_monitor'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PersistMessagesWithPostgresql("some connection string");

        opts.Durability.CapacityAwareAssignment = true;
        opts.Durability.NodeLoadMonitor = new QueueDepthLoadMonitor();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CapacityAwareAssignment.cs#L36-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_node_load_monitor' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### The Two Thresholds

`NodeOverloadThreshold` (90 by default) is the line where a node starts *shedding* agents. The line where it starts
*receiving* them again sits 10 points lower. In between the two, a node neither sheds nor receives -- it just keeps
running what it already has.

A single threshold is probably what you expected, so it's worth saying why there are two. If the shed line and the
receive line were the same number, a node sitting right at the threshold would shed an agent, drop below the line,
immediately look like a valid target again, and get the agent right back on the next evaluation. The band gives the
reading somewhere to settle.

`OverloadShedBatchSize` (1 by default) caps how many agents per scheme come off an overloaded node in any one
evaluation. Shedding only ever happens when some *other* node can take the work, too. An overloaded node with nowhere
to shed to keeps what it's running, because a node that's struggling is still better than no node at all.

### How Hard That Line Is Depends on Where the Agent Can Go

This part surprises people, so it's worth being explicit about. The overload threshold is a hard rule in some
distribution paths and only a strong preference in others.

For plain even distribution, it's a hard line. Every node is a candidate there, so refusing the overloaded ones can only
ever delay an agent, and waiting is better than piling more work onto a node that's already in trouble.

For the capability-aware paths -- group affinity for multi-database event stores, blue/green deployments across mixed
capabilities, and the durability agent spread -- it's a preference instead. Those candidate sets have already been
narrowed down by what each node actually declares it can run, and stacking a second hard constraint on top can empty a
set completely. An empty candidate set in those paths isn't "the agent waits a bit," it's a shard database with nothing
running against it and no self-heal until somebody restarts something. So a node with headroom always wins, but an
overloaded node still beats nothing at all.

::: tip
Group affinity will move a whole partition off an overloaded node, but only when another candidate can take the entire
thing. A shard database's agents are never split up just to relieve memory pressure.
:::

### When Nobody Has Headroom

If every node in the cluster is over the line, the leader deliberately leaves agents unassigned rather than piling them
onto a node that's already struggling.

That's the right call, but be aware that it's a *quiet* symptom compared to a crash loop. Nothing is throwing, nothing
is restarting, and the only outward sign is that some work isn't happening. If you're enabling this feature, it's worth
setting up an alert on agents that stay unassigned across several evaluations.

### Which Databases Support This

All of them. PostgreSQL, Sql Server, MySQL, Oracle, Sqlite, RavenDb, and Azure Cosmos Db all persist and read back a
node's advertised load.

::: tip
If you somehow end up with a message store that *doesn't* support the load advertisement, Wolverine logs a warning at
startup rather than letting you believe the feature is working. It'll still run, it just won't have any capacity
information to work with.
:::

### Where This Is Headed

Fair warning that this is the first piece of something bigger rather than a finished story. Today, the cluster has an
opinion about how much work a node should take instead of just dividing by node count and hoping. The obvious next step
is for it to have an opinion about how many nodes there should *be*, which is real dynamic scaling support. I don't
want to promise a date on that, but this is the foundation it would be built on, so the knob isn't meant to read as a
one-off.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L219-L235' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_persistence_metrics' title='Start of snippet'>anchor</a></sup>
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
