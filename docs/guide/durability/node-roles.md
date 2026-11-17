# Node Roles: HTTP, Messaging and Durability Nodes <Badge type="tip" text="7.0" />

Let's say your system has a busy HTTP API and a lot of background messaging, and you'd like to scale the two
separately. You want a pool of nodes that only serve HTTP requests, another pool that chews through your queues,
and a couple of nodes that do the durability work -- the inbox and outbox recovery, scheduled messages and so
on -- without your web tier ever getting involved. And you want to do that from one build of one application,
because that's what you actually have.

That's what `Durability.Workloads` is for. Every node of the application runs the same code against the same
message store, and each node is told which *workloads* it takes on:

| Workload | What the node does |
|---|---|
| `Messaging` | Starts your application listeners, runs the listener agents (exclusive, leader-pinned, dynamic and transport-supplied listeners), polls for scheduled messages and runs recurring schedules. Only a node with `Messaging` can be the leader |
| `Durability` | May hold the durability agents (`wolverinedb://`): inbox and outbox recovery, scheduled messages in tenant databases, dead letter and handled envelope expiry, node record sweeps. Needs `Messaging` too |
| `Projections` | May hold the event subscription agents (`event-subscriptions://`), meaning your async projections and subscriptions when Wolverine manages their distribution |

The default is `NodeWorkloads.All`, so nothing changes for anybody who doesn't set it. A node with
`NodeWorkloads.None` is an HTTP-only node.

## Setting it up

The setting is really one line, and you'll want it to come from your deployment configuration rather than code:

```cs
builder.Host.UseWolverine(opts =>
{
    opts.Durability.Mode = DurabilityMode.Balanced;

    // Wolverine__Workloads=Messaging,Projections in the environment,
    // and All if nothing says otherwise
    opts.Durability.Workloads = builder.Configuration.GetValue("Wolverine:Workloads", NodeWorkloads.All);
});
```

In Kubernetes that's three Deployments of the same image that differ only in that one environment variable:

| Deployment | `Wolverine__Workloads` |
|---|---|
| `api` | `None` |
| `workers` | `Messaging,Projections` |
| `durability` | `All` (or leave it out) |

If you'd rather say it in code, there are three presets that do nothing but set `Workloads`:

```cs
opts.Durability.RunAsHttpOnlyNode();      // None
opts.Durability.RunAsMessagingNode();     // Messaging, Projections -- never holds the durability agents
opts.Durability.RunAsDurabilityNode();    // All, the default, named for symmetry
```

Here's the configuration the three nodes in Wolverine's own tests share, with only the workloads differing:

<!-- snippet: sample_node_workloads_from_configuration -->
<a id='snippet-sample_node_workloads_from_configuration'></a>
```cs
private static Task<IHost> startAsync(NodeWorkloads workloads)
{
    return Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.ServiceName = "node-workloads-5601";
            opts.Durability.Mode = DurabilityMode.Balanced;

            // The one line that differs between the three deployments. In a real application this
            // comes from deployment configuration, e.g. Wolverine__Workloads=Messaging,Projections
            opts.Durability.Workloads = workloads;

            opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName)
                .AutoProvision();

            // An application listener the messaging nodes start, and an HTTP-only node does not
            opts.ListenToPostgresqlQueue("intake");

            opts.Durability.HealthCheckPollingTime = 1.Seconds();
            opts.Durability.CheckAssignmentPeriod = 1.Seconds();

            // Balanced startup reads wolverine_nodes, so the schema has to exist before the runtime starts
            opts.Services.AddResourceSetupOnStartup();
        }).StartAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PostgresqlTests/Agents/node_workloads_host_5601.cs#L34-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_node_workloads_from_configuration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## What an HTTP-only node does and doesn't do

An HTTP-only node is still a full Wolverine node. It:

* Serves your HTTP (or gRPC) endpoints and runs `InvokeAsync()` inline, same as always
* Publishes through the transactional outbox, and delivers its own outgoing messages right after the transaction
  commits. If the broker is down, it retries them itself when the broker comes back
* Runs its local queues. A message an HTTP endpoint cascades to a local queue, durable or not, is handled
  **on the HTTP node**. If you want work off your web tier, route it to a broker or a database queue, not a local
  queue
* Listens to its control endpoint and to the dedicated reply queues of your transports, so it still takes part
  in the cluster and request/reply from an HTTP endpoint still works
* Heartbeats, migrates storage at startup and shows up in the node table like any other node

It never:

* Starts your application listeners
* Holds an agent of any kind
* Polls for scheduled messages. A scheduled send from an HTTP node is persisted, and a `Messaging` node's poller
  sends it when it comes due
* Becomes the leader

::: warning
"Dedicated reply queue" means an endpoint marked as used for replies. If you've marked one of your *application*
listeners with `UseForReplies()`, an HTTP-only node listens to that queue too, and will handle whatever application
messages arrive on it. Give replies a queue of their own if you don't want that.
:::

If an HTTP node dies with messages still in its outbox, those rows are released and the durability agent on a
`Durability` node sends them. That works because that node runs the same code and can build the same senders.

## Durability needs Messaging

The durability agent recovers incoming envelopes by running them through the *holding node's own* listeners. A
node holding durability agents with no listeners of its own would either dead letter other nodes' messages as
unknown or leave them sitting in the inbox, so Wolverine refuses to start a node with `Durability` but without
`Messaging`:

```
Durability.Workloads is 'Durability', which has Durability without Messaging. A node holding the durability agents
recovers inbox envelopes by running them through its own listeners, so it has to run Messaging too.
```

A [warm-up node](/guide/durability/marten/distribution#warming-up-a-new-release-on-its-own-nodes) without
`Projections` is refused too, because projections are the only thing a warm-up node does.

In `Solo` mode the one node has to do everything, so Wolverine ignores the setting there, logs a warning and runs
`All`. That's so your test harness can still run the application in `Solo` mode even when its configuration says
it's an HTTP-only node. `MediatorOnly` and the serverless modes ignore the setting as well.

## Who leads

Only a node with `Messaging` can be the leader, the same as a warm-up node never leads. The leader runs the
leader-pinned listeners, so it has to be a node that listens to anything at all.

If every node with `Messaging` goes away, there's no leader and nothing gets reassigned until one comes back.
Every remaining node logs this once:

```
No live node in this Wolverine cluster runs the Messaging workload (Durability.Workloads), so nothing consumes
this application's queues or runs its scheduled messages, and there is no leader to assign agents.
```

## When no node runs a workload

The leader never puts an agent on a node that doesn't run that agent's workload, and it moves one that's sitting
on such a node, even one an operator pinned there. If no live node runs the workload at all, the agent isn't
running anywhere, and it says why: its `UnplacedReason` is `NoNodeForWorkload`. The leader also logs it:

```
1 agent(s) need a node running the Durability workload, and no live node in this cluster runs it
(Durability.Workloads), so they are not running anywhere. Start a node with Durability: wolverinedb://default
```

That's a different thing from `AwaitingCapacity`. More headroom on the nodes you have won't place the agent,
only a node with the right workload will.

So the one thing to plan for is the durability pool. With a single `Durability` node, losing that node stops
inbox and outbox recovery until it, or another one, comes back. Run at least two.

## Your own agent families

A custom `IAgentFamily` is treated as messaging work unless it says otherwise:

```cs
public class MyAgentFamily : IAgentFamily
{
    // The default is NodeWorkloads.Messaging, so you only need this
    // when your agents belong somewhere else
    public NodeWorkloads RequiredWorkload => NodeWorkloads.Projections;

    // ...
}
```

## Capacity-aware assignment and group affinity

Workloads are a hard filter, and [capacity-aware assignment](/guide/durability/leadership-and-troubleshooting#capacity-aware-agent-assignment)
works within whatever nodes are left. An overloaded node only sheds agents to another node running the same
workload, and the only `Durability` node under pressure keeps its durability agents rather than shedding them to
nowhere.

For a sharded, database-per-tenant event store, each shard database's projections still run together on one
node, chosen from the `Projections` nodes. The durability agent of a database normally follows its projections to
the same node so the database only sees one node's connection pool, but it can't follow them to a node without
`Durability`. If your projection nodes don't run `Durability`, each shard database ends up with two nodes'
connection pools instead of one. If that matters for your [connection budget](/guide/durability/connection-budgets),
give your projection nodes `Durability` too.

## Rolling it out

::: warning
Every node of one message store has to run the same build. Workloads change what a node does, never which
handlers it has. During a blue/green rollout that means the same build per color.
:::

* The workloads travel to the leader as a marker in each node's capabilities, so there's no database change. A
  node without the marker, which is every 6.x node and every node left on the default, reads as `All`
* Roll workloads out only after every node runs Wolverine 7.0. During a rolling upgrade a 6.x leader can still
  send listeners to an HTTP-only node, because its even spread doesn't look at capabilities. The HTTP-only node
  refuses to start them, so nothing runs there, but the leader keeps reassigning them until it's on 7.0 itself.
  Expect that churn for the length of the upgrade
* `opts.Durability.DurabilityAgentEnabled = false` is **not** a way to make a messaging node. It also turns off
  storage migration, the outbox batcher and inbox recovery loops on that node. Use `Workloads` instead

::: tip
If you use Marten's own `AddAsyncDaemon(DaemonMode.HotCold)`, workloads don't govern it, and an HTTP node would
happily take the async daemon. Use
[Wolverine-managed distribution](/guide/durability/marten/distribution) so `Projections` decides where projections
run, or `DaemonMode.Disabled` on your HTTP nodes.
:::

`describe` shows a node's `Workloads` under its durability settings.
