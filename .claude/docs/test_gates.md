# Test suites as Stoat gates (GH-5019)

How to run a Wolverine test suite as a gate in a Stoat plan (`stoat run-plan`, the
`serverless-with-outbox` plan of #5016, and so on): the command per suite, what the run publishes,
the Docker services each suite needs, and what can safely run side by side on one box.

## The command

Every gate is a Nuke target, run from the root of the worktree under test:

```bash
BOBCAT_RUN_TAG={plan}/{node} ./build.sh <Target> --framework net9.0
```

- **Exit code 0 is green.** A target fails if any of its projects fails.
- The target builds the test projects it runs, starts the Docker services it needs (see
  [Docker services](#docker-services)), and runs each project through Bobcat's supervisor
  (`build/SupervisedTests.cs`): Microsoft.Testing.Platform workers, a fresh-process retry within a
  budget, stall detection, a heartbeat, and the `[Trait("Category", "Flaky")]` exclusion. That is
  the same run CI does; the GitHub workflows call the same targets.
- `--framework net9.0` matches CI (`.nuke/parameters.json`). Projects that only target net10.0
  (the Alba-based suites, Polecat) pick net10.0 on their own.
- **Set `BOBCAT_RUN_TAG` exactly.** It is the only thing that ties the run to the plan node, and the
  gate matches it literally. Do not invent a tag, shorten it or add a timestamp; use the one the plan
  gave the agent (`{plan}/{nodeId}`).

## LocalGate: every suite at once (GH-5125)

`./build.sh LocalGate` runs what the CI workflows run (tests.yml, dotnet.yml, http.yml, fsharp.yml,
command-line.yml, bobcat-samples.yml) on one box through Bobcat's Gate. It is the command a plan's
"green before push" step runs:

```bash
stoat gate run {plan}/{node}                        # from the worktree; the node's step is ./build.sh LocalGate
./build.sh LocalGate                                # by hand: the suites, then the extras
./build.sh LocalGateSuites                          # the suites alone
./build.sh LocalGateSuites --gate-only Marten Http  # only suites whose project name contains one of these
./build.sh LocalGate --framework all                # every framework each suite declares (EF Core 9 and 10)
```

- **Release**, as in CI, for every target it runs, unless `--configuration` is given.
- **Build once.** It starts (or reuses) every docker-compose service the selected suites need, builds
  them while the containers boot (one `dotnet build` per framework over a generated solution in
  `artifacts/local-gate/`), then waits for the services' readiness probes.
- **One unit per test project.** The CI shards of a project (CIMarten / CIMartenDistribution /
  CIMartenTenancy, the SQL Server, Polecat, ASB and AWS shards) are one unit here. Each unit's
  supervisor is configured exactly as its CI target's is (`configureSupervisor` in
  `build/SupervisedTests.cs`), and leaves its ledger in `artifacts/test-ledger/LocalGate.*.json`.
- **Framework.** `.nuke/parameters.json` pins `net9.0`, as in CI; a suite that does not target it runs
  on its newest. CI's EF Core 10 job (`CIEfCoreNet10`) is only covered by `--framework all` or
  `--framework net10.0`.
- **Longest first, under one worker budget** (`--gate-workers`, default half the cores). Durations
  are kept in `.bobcat/gate-durations.json` for the next run's order.
- **Extras**, which `LocalGate` runs after the suites, one after another, as the ordinary targets
  they are: `CICommandLineSmoke` (describe-handlers and event-model), `CIAotSmoke`, `BobcatSamples`,
  `CICodegenTest`, then `CICodegenDrift`. The codegen pair rewrites the generated tree; drift goes last
  and regenerates it, so a green run leaves the tree as it was. A red one leaves the regenerated files
  for you to read (`git checkout -- .` puts them back). Every extra runs even when an earlier one is red.
- **The list** is the table at the top of `build/LocalGate.cs`; `build/gates.json` is generated from it
  (`./build.sh LocalGateManifest`). `VerifyLocalGateCoverage` (part of `./build.sh ci`) fails when a
  test project in `wolverine.slnx` is in neither the table nor its `NotGated` list, or when
  `gates.json` is stale.

How the suites share the box (the tracks; a track is held by one unit at a time):

| Resource | Rule |
|---|---|
| Postgres, SQL Server | A suite that only uses the database its connection string names gets its own, `wolverine_gate_<project>`, recreated per run and handed over as `ConnectionStrings__postgres` / `ConnectionStrings__sqlserver`; it holds no track. A suite that also creates sibling databases under fixed names (MartenTests, PostgresqlTests, PersistenceTests, the EF suites, PolecatTests) runs against the default database, as in CI, and holds the `postgresql` / `sqlserver` track. SqlServerTests keeps its four `wolverine_w*` lanes. |
| Each broker and emulator | One track per compose service: rabbitmq, kafka, localstack, asb-emulator, pulsar, gcp-pubsub, nats, mysql, oracle, azurite, fake-gcs-server. |
| Fixed ports, heavy emulators | `mqtt-broker-ports` (both MQTT suites host a broker on 1883/1884), `cosmos-emulator` (each Cosmos suite starts one through Testcontainers), `ravendb`. |
| Nothing | CoreTests, SQLite, Fisher, gRPC, the extension and F# suites: the worker budget only. |

## What the run publishes

Each supervised project run announces itself to a Bobcat monitor when one answers at
`BOBCAT_MONITOR_URL` (default `http://localhost:5525`; a local Stoat host or `bobcat ui` both
listen there). `PublishToMonitor` is on in `build/SupervisedTests.cs`, so the supervisor:

- owns the run bracket: `run_started` with mode `supervised`, the suite (the test project's name),
  the repository path and branch, `BOBCAT_RUN_TAG`, and the true post-filter test total; heartbeats;
  `run_finished` with passed, failed, passed-on-retry and indeterminate counts and the exit code;
- forwards every test's start and verdict as it happens, plus stalls, scheduled retries, lane and
  worker lifecycle, and worker faults;
- hands `BOBCAT_RUN_ID` and `BOBCAT_RUN_OWNER` to its workers, so a worker that publishes for itself
  (the Bobcat.Xunit spec projects below) lands on the same run instead of opening its own.

**A target that runs several projects publishes one run per project, all with the same tag.**
`CIPersistence` publishes `PersistenceTests` and `PostgresqlTests`; `CIMarten` publishes `MartenTests`
and `MartenSubscriptionTests`. A gate on such a target is green when every run under the tag is.

With no monitor listening, as on every CI runner, the probe costs one refused local connection and
nothing is published. `BOBCAT_MONITOR=0` turns publishing off.

### What published before GH-5019

- **No supervised suite published anything.** `Supervisor.PublishToMonitor` is opt-in and the build
  never set it. Stoat could only see these suites through `stoat gate exec` (exit code and commit).
- **The four Bobcat.Xunit spec projects** (`Wolverine.Bobcat.Tests`, `Wolverine.Bobcat.Http.Tests`,
  `IncidentService.Tests`, `ProcessManagerViaHandlers.Tests`, all in `CIBobcat`) published their own
  scenario streams through Bobcat's MTP adapter, including the tag. Under the supervisor each worker
  opened its own run; now they join the supervisor's.

### Targets that cannot publish

`CIAotSmoke`, `CICodegenDrift` and `CICodegenTest` run publish and codegen commands, not test hosts,
so there is no supervised run to announce. Gate on them with `stoat gate exec`, which records the exit
code and commit.

## The suites a plan gates on

| Suite | Command | Docker services |
|---|---|---|
| CoreTests | `./build.sh CoreTests --framework net9.0` | none |
| PostgresqlTests (with PersistenceTests) | `./build.sh CIPostgresql --framework net9.0` and `./build.sh CIPersistence --framework net9.0` (two shards of PostgresqlTests; both make the whole suite) | postgresql, sqlserver, rabbitmq |
| MartenTests | `./build.sh CIMartenDistribution --framework net9.0`, `./build.sh CIMartenTenancy --framework net9.0` and `./build.sh CIMarten --framework net9.0` (three shards of one project; all three make the whole suite) | postgresql |
| EfCoreTests | `./build.sh CIEfCore --framework net9.0` | postgresql, sqlserver, rabbitmq |
| SqlServerTests | `./build.sh CISqlServer --framework net9.0` | sqlserver |

`CoreTests` is the one target that compiles the **whole solution** first (`Compile`, after
`Restore`/`Init`, which also empties `artifacts/` and `results/`); the `CI*` targets build only their
own projects.

## Every target

| Target | Test projects | Docker services | Notes |
|---|---|---|---|
| `CoreTests` | CoreTests | none | full-solution compile first |
| `CIMessageRouting` | MessageRoutingTests | rabbitmq | `./build.sh ci` runs this after CoreTests |
| `CIPostgresql` / `CIPersistence` | PostgresqlTests shards (+ PersistenceTests (net9.0) in `CIPersistence`) | postgresql (+ sqlserver, rabbitmq in `CIPersistence`) | |
| `CISqlServer` | SqlServerTests | sqlserver | 4 workers, one database per lane (`wolverine_w0..`) |
| `CIMySql` | MySqlTests | mysql | |
| `CIOracle` | OracleTests | oracle | the slowest image to become ready |
| `CISqlite` | SqliteTests | none | |
| `CIFisher` | FisherTests, InMemoryTests | none | |
| `CIEfCore` / `CIEfCoreNet10` | EfCoreTests (+ EfCoreTests.MultiTenancy on net10.0) | postgresql, sqlserver, rabbitmq | |
| `CIMartenDistribution` / `CIMartenTenancy` / `CIMarten` | MartenTests shards (+ MartenSubscriptionTests in `CIMarten`) | postgresql | |
| `CIPolecat` / `CIPolecatWorkflow` / `CIPolecatSagas` | PolecatTests shards (+ PolecatIncidentService.Tests in `CIPolecat`) | sqlserver | net10.0 |
| `CICosmosDb` | CosmosDbTests, CosmosDbTests.LeaderElection | none from compose | starts its own emulator through Testcontainers (Docker required, dynamic ports) |
| `CIRavenDb` | RavenDbTests, RavenDbTests.LeaderElection | none | embedded RavenDB server; net9.0 |
| `CIClaimCheck` | Wolverine.ClaimCheck.*.Tests | postgresql, sqlserver, fake-gcs-server, nats | |
| `CIAmazonS3` | Wolverine.AmazonS3.Tests | localstack | |
| `CIAzureBlobStorage` | Wolverine.AzureBlobStorage.Tests | azurite | |
| `CIAWSSqs` / `CIAWSSqsCompliance` / `CIAWSSns` (`CIAWS` runs all three) | Wolverine.AmazonSqs.Tests, Wolverine.AmazonSns.Tests | localstack, postgresql | |
| `CIAzureServiceBusLeader` / `CIAzureServiceBusRouting` / `CIAzureServiceBus` | Wolverine.AzureServiceBus.Tests shards | asb-emulator, postgresql | asb-emulator brings asb-sql |
| `CIRabbitMQ` | Wolverine.RabbitMQ.Tests | rabbitmq, postgresql, sqlserver | |
| `CICircuitBreaking` | CircuitBreakingTests | rabbitmq, postgresql | |
| `CIKafka` | Wolverine.Kafka.Tests | kafka, postgresql | |
| `CINATS` | Wolverine.Nats.Tests | postgresql | |
| `CIPubsub` | Wolverine.Pubsub.Tests | gcp-pubsub, postgresql | |
| `CIPulsar` | Wolverine.Pulsar.Tests | pulsar, postgresql | |
| `CIRedis` | Wolverine.Redis.Tests | postgresql | |
| `CIMQTT` | Wolverine.MQTT.Tests | postgresql, sqlserver | |
| `CIMQTT5` | Wolverine.Mqtt5.Tests | postgresql, sqlserver | |
| `CIHttp` | Wolverine.Http.Tests | postgresql | |
| `CIHttpAspVersioning` | Wolverine.Http.AspVersioning.Tests | none | net10.0 |
| `CIGrpc` | Wolverine.Grpc.Tests | none | |
| `CIOtel` | TracingTests | rabbitmq | |
| `CIBobcat` | Wolverine.Bobcat.Tests, Wolverine.Bobcat.Http.Tests, IncidentService.Tests, ProcessManagerViaHandlers.Tests | postgresql, sqlserver | Bobcat.Xunit spec projects |
| `CIExtensions` | Wolverine.AI, DataAnnotationsValidation, FluentValidation, MemoryPack, MessagePack and Protobuf tests | none | |
| `CISlowTests` | SlowTests | postgresql, rabbitmq | wall-clock bound; not in the CI matrix |
| `CIAotSmoke` | (AOT publish smoke apps) | postgresql | not a supervised test run |
| `CICodegenDrift` / `CICodegenTest` | (codegen commands) | postgresql, rabbitmq, sqlserver / rabbitmq | not a supervised test run |

The service names are the `docker-compose.yml` service names; the ports are in that file and in the
CLAUDE.md table.

## Docker services

A target starts only the services it names, with `docker compose up -d`, then waits on a real
readiness probe for each (`AwaitDockerServices`). A service whose host port already answers is
**reused, not started**: every worktree is its own compose project, but the ports are fixed, so a
second worktree's `compose up` used to fail with "port is already allocated" while the first
worktree's stack was running. Now every worktree on the box shares whichever stack came up first.

Bringing a stack down is the box owner's call, not a gate's: `docker compose down` in one worktree
stops only that worktree's containers, but other worktrees may be using them.

## Running suites side by side

For Stoat's resource declarations (JasperFx/stoat#101):

- **LocalGate already does all of this** for the whole CI set, with a finer rule for Postgres and SQL
  Server (a database per suite where the suite allows it); see [LocalGate](#localgate-every-suite-at-once-gh-5125).
  What follows is for scheduling separate targets yourself.
- **Treat each Docker service as an exclusive resource for the length of a run.** The suites are
  written to own their services: schema names, queue names, exchange names and topics are hard-coded,
  and fixtures drop and recreate them. Two suites that share a service collide, even from different
  worktrees, because they share the one container. Two suites whose service sets are disjoint can run
  at the same time.
- **The per-lane databases are shared too.** `CISqlServer` runs four workers against `wolverine_w0..3`
  on SQL Server (the Postgres equivalent, `postgresDatabasePerLane`, is not used by any target today),
  so two runs that use lanes collide on those names.
- **Suites with no service** (`CoreTests`, `CISqlite`, `CIFisher`, `CIRavenDb`, `CIGrpc`,
  `CIHttpAspVersioning`, `CIExtensions`) only compete for CPU and memory. `CICosmosDb` starts its own
  emulator on dynamic ports, so it does not collide either, but the emulator is heavy.
- **One target per worktree at a time.** The build writes `bin/` and `obj/` in place, and `CoreTests`
  compiles the whole solution, so two gates in one worktree race on the same outputs.
- **CPU.** The supervisor clamps a target's workers to half the machine's cores, so a box running
  several suites still oversubscribes. Budget roughly one core per worker plus the containers.
