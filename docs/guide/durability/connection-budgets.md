# Connection Budgets

::: tip
Introduced in Wolverine 6.19. This is a measurement feature — Wolverine reports connection pressure,
it does not yet react to it.
:::

Database connections are a resource of the **server**, not of a logical database. That distinction
does not matter much when your application talks to one database, but it matters a great deal in a
sharded multi-tenancy deployment, where hundreds of tenant databases can live on a handful of
Postgres clusters and every node in your cluster is drawing from the same finite pool. When that
pool runs dry the failures are ugly and diffuse: schema migrations that can't get a connection,
projection daemons that stall, health checks that flap.

A *connection budget* makes that pressure visible. Wolverine reports, per database server:

| Value | Meaning |
|-------|---------|
| **used** | Connections currently open on the server, by every application sharing it |
| **max** | The budget you declared for that server |

## Turning it on

The budget machinery is on automatically when your message stores span statically-known multiple
databases — Marten's `MultiTenantedWithShardedDatabases`, the many-databases-per-server shape the
feature exists for. It is off for a single database, where a per-server number tells you nothing a
per-database number doesn't.

Declaring a budget is itself an opt-in, so this is all you normally write:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Durability.ConnectionBudgets
            .ForServer("pg-shard-a", 5432, maxConnections: 400)
            .ForServer("pg-shard-b", 5432, maxConnections: 200);
    }).StartAsync();
```

Budgets are declared per server because one deployment routinely spans servers with different
limits. The port is part of the key, so two clusters co-hosted on one host stay distinct.

For SQL Server, pass the `Data Source` verbatim — it already carries the port or named instance:

```csharp
opts.Durability.ConnectionBudgets.ForServer("sql-1,1433", maxConnections: 500);
```

`opts.Durability.ConnectionBudgets.Enabled` forces the whole thing on or off if you disagree with
the automatic rule.

## Why the max is configuration and not `max_connections`

Wolverine deliberately does **not** treat the server's own `max_connections` as your budget.

If there is any pooler in front of the database — pgBouncer being the usual one — the server's limit
describes how many connections *the pooler* may open, which is not remotely the same question as how
many your application is entitled to take. Charting utilization against it would be reassuring and
wrong.

So the budget is something you declare, and the probe supplies only the `used` side. When you
haven't declared one, Wolverine falls back to reading the server's limit
(`max_connections` / `@@MAX_CONNECTIONS`) and labels the reading as *probed* so you can tell a
declared budget from a guessed one. If even that can't be read, the budget is reported as *unknown*
— the used count is still published, but no utilization is computed.

## What the numbers actually mean in your topology

::: warning
Read this before you alert on the number.
:::

**The used count is server-wide and includes other applications.** It comes from
`sum(numbackends)` on PostgreSQL and `count(*) from sys.dm_exec_connections` on SQL Server. That is
the point — the connections are a shared resource and the contention is real regardless of who is
causing it — but do not read the number as "connections Wolverine is using."

**Behind a transaction-pooling pgBouncer, the used count is not your client connections.** It counts
pgBouncer's backend connections to Postgres, which is exactly what you want to watch (that is the
scarce pool), but it will look far smaller than the number of client sessions your application
believes it has. In session-pooling mode the two track much more closely. In a direct-connection
topology, the used count *is* your connections plus everyone else's.

## Permissions

The PostgreSQL probe reads `pg_catalog.pg_stat_database`, which is visible to any user without
special grants.

The SQL Server probe reads `sys.dm_exec_connections`, which requires **`VIEW SERVER STATE`**. Some
locked-down hosting does not grant it. When it is missing, Wolverine logs a single warning per server
and reports that server's budget as unknown rather than failing the metrics sweep.

## What gets published

**OpenTelemetry**, as gauges tagged by `server`:

| Instrument | Meaning |
|------------|---------|
| `wolverine-database-connection-count` | Connections currently open on the server |
| `wolverine-database-connection-budget` | The budget, omitted entirely when unknown |

**`IWolverineObserver.ConnectionBudget`**, which is how CritterWatch surfaces it:

```csharp
public record ConnectionBudgetSnapshot(
    DatabaseServerId Server,
    int Used,
    int? Max,
    ConnectionBudgetSource Source); // Configured | Probed | Unknown
```

## Startup validation <Badge type="tip" text=".NET 11" />

On .NET 11, Wolverine also registers an `IAsyncStartupValidator` (the new
`Microsoft.Extensions.Options` contract that the host runs once, before any hosted service starts)
over the declared budgets. It does nothing unless at least one budget has been declared with
`ForServer(...)`, and nothing when `ConnectionBudgets.Enabled` is explicitly `false`. When it runs:

* **A budget that names a server no message store lives on refuses startup** with an
  `OptionsValidationException` that lists the unmatched budget and the servers Wolverine does know
  about. That budget is a typo or a stale entry, and it would otherwise sit silently unused. Matching
  is the same as for the metrics: case-insensitive host names, and a host-wide `ForServer(host, max)`
  covers every port on that host.
* **Each known server is probed once**, and a warning is logged when its open connections already
  meet or exceed the declared budget. This does *not* refuse startup: during a rolling or
  blue/green deploy the other nodes legitimately hold most of the budget, so a full budget is
  normal for the newest node. A probe that fails is logged, never fatal.

The check only sees message stores that are known before Wolverine starts. Databases that a
multi-tenanted message store discovers at runtime (sharded or dynamic tenancy) cannot be seen this
early, so when the application has any multi-tenanted message store an unmatched budget is logged as
a warning instead of refusing startup. Budgets declared only from an `IAsyncWolverineExtension` are
not seen by the validator either. Codegen and OpenAPI tooling runs skip it entirely.

On .NET 10 there is no such validator; the budgets behave exactly as described above.

## Cost

The probe rides the existing durability metrics sweep (`Durability.UpdateMetricsPeriod`, 5 seconds
by default) and is deduplicated by server: **one query per server per sweep**, however many tenant
databases the node happens to own. A node owning 200 tenant databases across 2 servers issues 2
connection probes per sweep, not 200. Probing per database would multiply the very pressure the
number exists to reveal.

The whole feature is silent when `Durability.DurabilityMetricsEnabled` is `false`.

## Reacting to pressure

Not yet. Today Wolverine measures and reports; it does not throttle itself when the budget gets
tight. Adaptive back-off — scaling daemon polling and concurrency down under pressure, with
hysteresis so a single threshold doesn't flap — is planned as a follow-up and will be opt-in. See
[GH-3397](https://github.com/JasperFx/wolverine/issues/3397).
