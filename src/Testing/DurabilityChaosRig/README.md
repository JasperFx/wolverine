# Durability chaos rig

An out-of-process crash rig for Wolverine's durability: the outbox, the durable inbox, the database queue
transports, scheduled and retried messages, dead letter replay and the node agents that recover all of them.
A driver runs a producer and several worker processes against a real database (and broker), injects faults at
random from one seed, then checks a ledger of what was sent against what was processed.

It found the envelope id collisions between processes fixed in GH-5399, the non-atomic scheduled retries on
the SQL Server, MySQL, Oracle and SQLite queues fixed in GH-5408, and the ambiguous EF Core commit in GH-5410.

Like `src/Testing/KafkaPerfRig`, it is **not** in either `.slnx` solution and not in CI. Build and run it
on its own.

## Build and run

```bash
MSBUILDDISABLENODEREUSE=1 dotnet build src/Testing/DurabilityChaosRig -c Release

cd src/Testing/DurabilityChaosRig/bin/Release/net10.0
dotnet DurabilityChaosRig.dll driver 180 3 21     # [chaos seconds] [workers] [seed]
```

The driver starts the nodes with `dotnet DurabilityChaosRig.dll worker|producer`, so always run the built
dll rather than `dotnet run`. The exit code is 0 for a clean run and 1 otherwise.

## Roles

| Role | What it does |
|---|---|
| `driver [seconds] [workers] [seed]` | Runs a whole round. Defaults: 120 seconds, 3 workers, a random seed. |
| `worker` | Handles `Work`, `LocalStep` and `DelayedStep` in Balanced mode with short durability timings. |
| `producer` | Commits batches of `Sent` ledger rows together with their `Work` messages through the outbox. |
| `report` | Reads the ledger of the run in `CHAOS_DIR` again and prints the report. |

### What the messages do

- The **producer** commits ten `Sent` rows and ten `Work` messages per transaction: Marten's
  `OutboxedSessionFactory` for `CHAOS_STORE=marten`, `IDbContextOutbox` for `CHAOS_STORE=ef`.
- A `Work` message may:
  - cascade a `LocalStep` to the durable local queue `chaos-local`
  - schedule a `DelayedStep` two seconds later
  - fail on its first attempt, so it goes through a `ScheduleRetry`
  - fail as "poison" until the driver ships a fix, after which it is replayed from the dead letters
- Every handler writes a `Processed` ledger row in its own transaction, which also marks the inbox row
  handled. Two rows for one message and stage is a committed duplicate.

## Faults

The driver waits 2 to 7 seconds between faults and logs each one to `schedule.log`.

| Fault | What happens |
|---|---|
| `kill` | SIGKILL a worker or the producer, then restart it. |
| `term` | SIGTERM, a graceful stop, then restart it. |
| `pause` | SIGSTOP a worker past the 10 second stale node timeout, then SIGCONT. It wakes up after its agents moved. |
| `dbdown` | `docker pause` the database container for a few seconds. |
| `brokerdown` | `docker pause wolverine-rabbitmq-1` for a few seconds. Only with `CHAOS_TRANSPORT=rabbit`. |

After the chaos period the driver stops the producer, resumes anything paused, and polls the ledger every
5 seconds until the run is clean or stops changing for 3 minutes.

## Knobs

| Variable | Values | Default |
|---|---|---|
| `CHAOS_STORE` | `marten` (PostgreSQL), `ef` (EF Core on SQL Server), `efpg` (EF Core on PostgreSQL), `mysql`, `oracle` or `sqlite` | `marten` |
| `CHAOS_TRANSPORT` | `pgqueue`, `rabbit`, `sqlqueue`, `mysqlqueue`, `oraclequeue` or `sqlitequeue` | `pgqueue` |
| `CHAOS_FAULTS` | weights, e.g. `kill:6,term:2,pause:2,dbdown:1,brokerdown:1`. Add `net:N` with `CHAOS_TOXIPROXY=1`. Empty means a calm baseline. | `kill:6,term:2,pause:2,dbdown:1,brokerdown:1` |
| `CHAOS_HANDLER_DELAY` | `min-max` milliseconds every `Work` handler waits, e.g. `500-3000` for long handlers | `0-40` |
| `CHAOS_RETRY_COOLDOWN` | seconds a first-attempt failure waits before its `ScheduleRetry` | `1` |
| `CHAOS_TENANTS` | `1` (`CHAOS_STORE=marten` only) gives each tenant its own database, see below | off |
| `CHAOS_TENANT_PORT` | the port of the PostgreSQL that holds tenant `blue` | `5445` |
| `CHAOS_TOXIPROXY` | `1` routes the nodes' PostgreSQL traffic through Toxiproxy and enables the `net` fault | off |
| `CHAOS_DB_CONTAINER` | the container `dbdown` pauses, when it is not the compose default | per store |
| `CHAOS_POISON` | the share of `Work` messages that are poison, e.g. `0.05` | off |
| `CHAOS_SHORT_TIMEOUTS` | `1` adds `Command Timeout=5` to the connection string, which produces commits where "the error may mean it succeeded" | off |
| `CHAOS_SCHEMA` | the schema for the ledger and the message store. The queue tables go in `{schema}_queues`. | `chaos` |
| `CHAOS_DIR` | where the logs go | a new folder under `$TMPDIR/durability-chaos` |

Each database queue needs its own store: `pgqueue` with `marten` or `efpg`, `sqlqueue` with `ef`, `mysqlqueue`
with `mysql`, `oraclequeue` with `oracle`, `sqlitequeue` with `sqlite`. `rabbit` works with any store.

The `mysql`, `oracle` and `sqlite` stores have no transactional middleware for a plain connection, so
`AdoWorkHandler` does it by hand: one ADO.NET transaction holds the ledger row, the cascaded messages through an
enlisted `DatabaseEnvelopeTransaction`, and the mark that retires the envelope's inbox row. The driver creates their
`sent` and `processed` tables (`chaos_sent` and `chaos_processed` on Oracle). Oracle always uses the `WOLVERINE`
schema, whatever `CHAOS_SCHEMA` says, and SQLite uses `chaos.db` in `CHAOS_DIR`. SQLite takes one writer at a
time, so run it with one worker (`driver 120 1 <seed>`); there is no `dbdown` for it. The driver drops and recreates
both schemas at the start of each run, and on RabbitMQ deletes `chaos-work` and purges
`wolverine-dead-letter-queue`.

Examples:

```bash
# Calm baseline on the PostgreSQL queue
CHAOS_FAULTS= dotnet DurabilityChaosRig.dll driver 30 3 1

# RabbitMQ with Marten, default faults
CHAOS_TRANSPORT=rabbit dotnet DurabilityChaosRig.dll driver 120 3 7

# SQL Server with EF Core, 5 second command timeouts and poison messages
CHAOS_STORE=ef CHAOS_TRANSPORT=sqlqueue CHAOS_SHORT_TIMEOUTS=1 CHAOS_POISON=0.05 \
  dotnet DurabilityChaosRig.dll driver 180 3 23
```

### Network faults with Toxiproxy

`CHAOS_TOXIPROXY=1` (PostgreSQL stores only) points the nodes at a Toxiproxy proxy on port 25433 instead of the
database, and the `net` fault adds one toxic for 3 to 10 seconds: 1 to 6 seconds of latency, `reset_peer` on half
the traffic, or a black hole that closes connections after 4 seconds. The driver itself connects directly.

```bash
docker run -d --name wolverine-toxiproxy -p 8474:8474 -p 25433:25433 ghcr.io/shopify/toxiproxy:2.9.0

CHAOS_TOXIPROXY=1 CHAOS_SHORT_TIMEOUTS=1 CHAOS_FAULTS=net:6,kill:1 dotnet DurabilityChaosRig.dll driver 120 3 61
```

The proxy forwards to `host.docker.internal:5433` (`CHAOS_TOXIPROXY_UPSTREAM`), and the API is at
`http://localhost:8474` (`CHAOS_TOXIPROXY_API`).

### A database per tenant

`CHAOS_TENANTS=1` turns the Marten store into Marten's database-per-tenant tenancy. Tenant `red` is the
`{schema}_tenant` database on the main PostgreSQL, and tenant `blue` is a database with the same name on a second
PostgreSQL at `CHAOS_TENANT_PORT`. The producer alternates batches between the two tenants, and the report adds up
the ledger and the inboxes over the main database and both tenant databases. With
`CHAOS_DB_CONTAINER=wolverine-tenant-pg`, a `dbdown` fault takes only `blue` away. The main database (queues,
nodes, the main inbox) and `red` keep working.

The two tenant databases share a name on one host on purpose, because that is what GH-5435 was: their agent Uris
collided, and `blue` never got a durability agent.

```bash
docker run -d --name wolverine-tenant-pg -p 5445:5432 -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres \
  postgres:17 -c max_connections=300

CHAOS_TENANTS=1 CHAOS_DB_CONTAINER=wolverine-tenant-pg CHAOS_FAULTS=kill:4,term:2,pause:2,dbdown:3 \
  dotnet DurabilityChaosRig.dll driver 120 3 81
```

## Containers

The rig connects through `src/Servers.cs`, so it uses Wolverine's own `docker compose` services.

| Mode | Containers |
|---|---|
| `CHAOS_STORE=marten` | `wolverine-postgresql-1` |
| `CHAOS_STORE=ef` | `wolverine-sqlserver-1` |
| `CHAOS_TRANSPORT=rabbit` | `wolverine-rabbitmq-1` as well |
| `CHAOS_STORE=mysql` | `wolverine-mysql-1`, or any MySQL 8 through `WOLVERINE_MYSQL` and `CHAOS_DB_CONTAINER` |
| `CHAOS_STORE=oracle` | `wolverine-oracle-1`, or any Oracle Free 23 through `WOLVERINE_ORACLE` and `CHAOS_DB_CONTAINER` |
| `CHAOS_STORE=sqlite` | none |

Weasel's own containers hold ports 3306 and 1521 on a machine that also works on Weasel. Run MySQL and Oracle on
spare ports instead and point the rig at them:

```bash
docker run -d --name wolverine-mysql-3316 -p 3316:3306 -e MYSQL_ROOT_PASSWORD=P@55w0rd -e MYSQL_DATABASE=wolverine \
  mysql:8.0 --max-connections=500
docker run -d --name wolverine-oracle-1531 -p 1531:1521 -e ORACLE_PASSWORD=P@55w0rd -e APP_USER=wolverine \
  -e APP_USER_PASSWORD=wolverine -v $PWD/docker/oracle:/container-entrypoint-initdb.d gvenzl/oracle-free:23-slim

export WOLVERINE_MYSQL="Server=localhost;Port=3316;Database=wolverine;User=root;Password=P@55w0rd;"
export WOLVERINE_ORACLE="User Id=wolverine;Password=wolverine;Data Source=localhost:1531/FREEPDB1"
CHAOS_STORE=mysql CHAOS_TRANSPORT=mysqlqueue CHAOS_DB_CONTAINER=wolverine-mysql-3316 dotnet DurabilityChaosRig.dll driver 120 3 51
```

```bash
docker compose up -d postgresql sqlserver rabbitmq
```

`dbdown` and `brokerdown` pause these containers by name, so anything else using them during a run sees the
outage too.

## Reading the result

The last line of the driver's output, also written to `schedule.log`, starts with `CLEAN:` or `PROBLEMS:`.

A run is **clean** when every `Sent` message has exactly one `work` row, every `Work` that cascaded has
exactly one `local` and one `delayed` row, and nothing is left behind:

- no inbox rows still `Incoming` or `Scheduled`
- no outbox rows
- no database dead letters
- with RabbitMQ, nothing in `wolverine-dead-letter-queue`

The report also prints the slowest latency per stage, from the producer's commit to the handler's commit.

### Investigating a run that is not clean

The report prints the offending rows. Then:

1. Look up the fault times around the message in `schedule.log`. Times there are seconds since the driver
   started, and each line also has the clock time.
2. Look up the message's `Sent` and `Processed` rows in the ledger, and its inbox rows, matched by the
   message body.
3. Search the worker logs (`w1.log`, `w2.log`, ... and `producer.log` in `CHAOS_DIR`) for the envelope id.
4. Rerun the same seed to check whether it is deterministic. Faults come from the seed, but timing does not,
   so expect the shape to repeat rather than the exact rows.
5. File one issue per finding, with a deterministic test where possible. The rig is for finding bugs, not
   for proving fixes.

## Gotchas

- The driver starts each node under `/bin/sh -c 'dotnet ... ; true'` and sends signals to the `dotnet`
  grandchild. .NET's child process tracking spins once a **direct** child is SIGSTOPped, which stalls the
  driver itself.
- SqlClient refuses `InvariantGlobalization`, so do not turn it on in the project.
- Concurrent runs need different `CHAOS_SCHEMA` values. With `dbdown` they also need different database
  containers, because a pause takes down every run using that container.
- Build with `MSBUILDDISABLENODEREUSE=1`. Restarted nodes and leftover MSBuild nodes add up quickly.
- Several processes on one machine share an inbox here. Before GH-5399, envelope ids collided between them,
  which looked exactly like lost messages. Check for id collisions before theorising about recovery races.
