# GlobalPartitioning Rollout Plan (2026-07-18)

Goal: bring GlobalPartitioning topology support to every Wolverine messaging transport where it
makes sense, close the docs/test gaps on the transports that already have it, and sketch a
"native mode" v2 that leans on broker-native partitioning primitives.

## Current state (verified in code)

The core machinery lives in `src/Wolverine/Runtime/Partitioning/`:
- `GlobalPartitionedMessageTopology` pairs an external `PartitionedMessageTopology` (N broker
  endpoints named `{base}1..N`, exclusive listeners, forced `EndpointMode.Durable`) with a
  companion `LocalPartitionedMessageTopology` (`global-{base}` durable local queues).
- Slot routing is central: `Envelope.SlotForSending` / `SlotForProcessing` hash the resolved
  `GroupId` mod N (valid slot counts 3/5/7/9). Transports do **not** implement routing.
- `GlobalPartitionedRoute` shortcuts to the companion local queue when this node owns the
  exclusive listener for the slot; otherwise sends through the broker to the owning node.
- `GlobalPartitionedReceiverBridge` + `GlobalPartitionedInterceptor` handle inbound bridging and
  re-slotting of messages that arrive on non-sharded endpoints.

**A transport participates with exactly two pieces** (see Kafka/RabbitMQ as reference impls):
1. `PartitionedMessageTopologyWith{X} : PartitionedMessageTopology<TListenerCfg, TSubscriberCfg>`
   implementing `buildEndpoint`, `buildListener`, `buildSubscriber`
   (e.g. `src/Transports/Kafka/Wolverine.Kafka/Internal/PartitionedMessageTopologyWithTopics.cs`).
2. A pair of extension methods: `UseSharded{X}(this GlobalPartitionedMessageTopology, ...)`
   calling `SetExternalTopology(...)`, and `PublishToSharded{X}(this MessagePartitioningRules, ...)`
   (e.g. `KafkaTransportExtensions.cs:300-337`, `RabbitMqTransportExtensions.cs:479-515`).

### Support matrix

| Transport | `UseSharded*` | Dedicated global tests | In docs table (`partitioning.md`) |
|---|---|---|---|
| RabbitMQ | ✅ `UseShardedRabbitQueues` | ✅ | ✅ |
| Kafka | ✅ `UseShardedKafkaTopics` | ✅ (3 suites) | ✅ |
| Amazon SQS | ✅ `UseShardedAmazonSqsQueues` | ✅ | ✅ |
| Pulsar | ✅ `UseShardedPulsarTopics` | ❌ | ✅ |
| Azure Service Bus | ✅ `UseShardedAzureServiceBusQueues` | ❌ | ❌ |
| GCP Pub/Sub | ✅ `UseShardedPubsubTopics` | ❌ | ❌ |
| NATS | ✅ `UseShardedNatsSubjects` | ❌ | ❌ |
| Redis Streams | ✅ `UseShardedRedisStreams` | ❌ | ❌ |
| PostgreSQL DB queues | ❌ | — | — |
| SQL Server DB queues | ❌ | — | — |
| Amazon SNS | ❌ (publish-only transport) | — | — |
| MQTT | ❌ | — | — |
| SignalR | ❌ (non-goal, see below) | — | — |
| TCP / RavenDb control | ❌ (non-goals) | — | — |

## Wave 1 — Docs + test catch-up on the existing eight (cheap, ship first)

1. **Docs**: `docs/guide/messaging/partitioning.md` transports table (~line 501) only lists
   RabbitMQ, Kafka, SQS, Pulsar. Add Azure Service Bus, GCP Pub/Sub, NATS, and Redis with
   per-transport snippets (`UseShardedAzureServiceBusQueues`, `UseShardedPubsubTopics`,
   `UseShardedNatsSubjects`, `UseShardedRedisStreams`). Release-notes-worthy: half the support
   surface is currently invisible to users.
2. **Tests**: add a `global_partitioned_sharded_processing` suite (modeled on the Kafka/RabbitMQ
   ones) to ASB, GCP Pub/Sub, NATS, Redis, and Pulsar test projects. These are the transports
   where the extension exists but nothing exercises the full multi-node
   route→bridge→local-queue path against the real broker. Reuse the shared scenario shape from
   `src/Transports/Kafka/Wolverine.Kafka.Tests/global_partitioned_sharded_processing.cs`; if the
   duplication is annoying, consider lifting a shared scenario harness into
   `Wolverine.ComplianceTests`.

## Wave 2 — PostgreSQL + SQL Server database queues (the real feature work)

Best candidates: both already model multiple named durable queues
(`ListenToPostgresqlQueue(name)` / `ListenToSqlServerQueue(name)`), are inherently durable
(global partitioning forces `EndpointMode.Durable` anyway), have sticky per-node listener agents,
and give a zero-extra-infrastructure partitioning story for the "Postgres is my message broker"
crowd — a very on-brand critter-stack pitch.

Per database (Postgres first, SQL Server is a near-copy):

1. `PartitionedMessageTopologyWithDatabaseQueues :
   PartitionedMessageTopology<PostgresqlListenerConfiguration, PostgresqlSubscriberConfiguration>`
   in `src/Persistence/Wolverine.Postgresql/Transport/`.
   - `buildEndpoint` → `transport.QueueByName("{base}{n}")` equivalent (creates
     `wolverine_queue_{base}{n}` + scheduled table).
   - `buildListener` / `buildSubscriber` → wrap the existing listener/subscriber configurations.
2. Extensions `UseShardedPostgresqlQueues(...)` + `PublishToShardedPostgresqlQueues(...)` in
   `PostgresqlConfigurationExtensions` (and SqlServer twins).
3. **Verify exclusive listener semantics**: `ListenerScope.Exclusive` shard endpoints must be
   assigned/relocated by the agent coordinator the same way broker shards are. The sticky
   listener agents (`StickyPostgresqlQueueListenerAgent`) already exist — confirm they compose
   with `UsedInShardedTopology` endpoints and the `GlobalPartitionedRoute` "do I own the
   listener" check (`FindListeningAgent(...).Status == Accepting`).
4. **Table-name length**: shard suffixes push against the Postgres NAMEDATALEN identifier
   shortening (`PostgresqlQueue` line ~40) — add a test with a long base name.
5. **Multi-tenancy interaction**: DB-per-tenant queues (`MultiTenantedQueueListener`) × sharded
   topology — decide and document (simplest: shard tables exist per tenant database, slot
   routing unchanged; exclusivity is per-database).
6. Tests in `PostgresqlTests` / `SqlServerTests`: the standard global sharded-processing scenario
   plus a two-node concurrency test (mirror `Bug_concurrency_with_global_partitioning.cs`).

## Wave 3 — Decisions for the remainder

- **Amazon SNS**: recommend **no standalone implementation**. SNS is publish-only and always
  pairs with SQS; the sharded consumption story is already `UseShardedAmazonSqsQueues`. If a
  fan-out-then-shard story is wanted, the right shape is a convenience that publishes to one SNS
  topic and subscribes N sharded SQS queues — i.e. an SQS-side feature, not an SNS topology.
  Document this explicitly (non-goal with a pointer).
- **MQTT**: feasible as N sharded topics (`{base}1..N` topic names, exclusive Wolverine-assigned
  listeners), but two caveats to resolve first:
  1. Global partitioning forces `EndpointMode.Durable` on external slots; MQTT topics default to
     `BufferedInMemory` — verify Durable mode (DB-backed inbox) actually works on MQTT endpoints
     or the forced mode will need a transport-specific carve-out.
  2. Broker offers no exclusive-consumer primitive; exclusivity rests entirely on Wolverine's
     agent assignment (same as Redis, so acceptable — but QoS ≥ AtLeastOnce should be forced on
     shard topics).
  Priority: low. Do it only if user demand shows up; MQTT users are mostly IoT-ingest, not
  ordered-work-queue.
- **SignalR**: explicit **non-goal** — broadcast hub to browser clients, no competing-consumer or
  work-queue semantics to partition. Say so in the docs.
- **TCP / RavenDb**: non-goals (point-to-point / control-plane only).

## Wave 4 (v2, feeds off the capability research) — "native mode" partitioning

Today every transport implements global partitioning as N distinct endpoints with
Wolverine-computed hash slots — even Kafka uses N *topics*, not Kafka partitions. Several brokers
have native primitives that could back a `UseNative...` variant with fewer moving parts and
broker-managed rebalancing/failover:

| Transport | Native primitive | Notes |
|---|---|---|
| Kafka | topic partitions + consumer group (GroupId → partition key) | broker-managed assignment replaces exclusive-agent choreography; KIP-848 makes rebalances cheap |
| RabbitMQ | **super streams + single-active-consumer** | needs `RabbitMQ.Stream.Client`; no .NET framework exposes this today |
| Azure Service Bus | **sessions** (`SessionId` = GroupId) | already hinted in docs (`partitioning.md:372`); session state is a bonus |
| Amazon SQS | FIFO `MessageGroupId` | per-group ordering + exclusive in-flight group, high-throughput mode |
| GCP Pub/Sub | ordering keys (GroupId → `OrderingKey`) | already partially wired (`PubsubEndpoint` maps GroupId → OrderingKey) |
| NATS | deterministic subject mapping `{{partition(N,...)}}` + pinned pull consumers (2.11+) | the Orbit `pcgroups` model |
| Pulsar | `KeyShared` subscription (GroupId → key) | already supported as a subscription type; DotPulsar supports KeyShared |

This is a bigger design conversation (per-key vs per-slot ordering, poison-message head-of-line
blocking, how `GlobalPartitionedRoute`'s local-shortcut interacts with broker-managed
assignment) — park as a design doc after Waves 1–2 ship.

## Sequencing & sizing

| Wave | Size | Risk |
|---|---|---|
| 1 docs | small | none |
| 1 tests | medium (5 broker test suites, CI time) | flaky-broker risk; gate on per-transport CI jobs |
| 2 Postgres | medium | agent-assignment edge cases in multi-node tests |
| 2 SQL Server | small (clone of Postgres) | low |
| 3 decisions/docs | small | none |
| 4 native mode | large | design-first, separate effort |
