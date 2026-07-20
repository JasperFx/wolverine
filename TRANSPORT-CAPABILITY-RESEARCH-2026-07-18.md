# Wolverine Transport Capability Research (2026-07-18)

Gap analysis: what each underlying broker offers that Wolverine doesn't exploit, what competitor
frameworks expose that Wolverine lacks, and a ranked candidate list. Companion doc:
`GLOBAL-PARTITIONING-ROLLOUT-PLAN.md`.

Method: codebase inventory of every transport's actual surface (file refs verified), plus
web research on broker capabilities (2025–2026 state, official docs) and ~20 competitor
frameworks (.NET, JVM, Go, Python, Elixir).

## Strategic context

- **MassTransit v9 went commercial** (~$400–1,200/mo); v8 stays OSS but maintenance-only through
  ~end 2026, then EOL. The .NET competitor with the largest feature overlap is exiting free OSS —
  gaps closed in the next year land exactly when its users re-evaluate.
- **NServiceBus** has no Kafka transport, no GCP Pub/Sub transport, and explicitly does not
  support ASB sessions. Its moat is ops tooling (ServicePulse redrive UX) — a CritterWatch target
  list, not a framework gap.
- **Wolverine's uncontested lanes**: GCP Pub/Sub (neither MT nor NSB has one), Pulsar (no major
  .NET framework has one), NATS JetStream (only SlimMessageBus core-NATS + an embryonic Rebus
  port), Redis Streams (SlimMessageBus is at-most-once lists/pub-sub only). Deepening these
  extends leads nobody contests.

---

## Per-transport gap analysis

### RabbitMQ

Already strong: queue types classic/quorum/stream, full x-arguments, native DLX + recovery
bridge, MT/NSB interop, vhost-per-tenant, conventional routing.

Unexploited broker capabilities:
1. **Streams as streams** — queue-type `stream` is declarable but there is no offset/replay
   surface (`x-stream-offset` first/last/next/timestamp), no `RabbitMQ.Stream.Client`, and no
   **super streams + single-active-consumer** (Rabbit's Kafka-style partitioned ordered
   consumption). Spring AMQP has all of this; **no .NET framework does** → differentiator.
   Also stream filtering: Bloom (3.13), AMQP property filters (4.1), SQL filters (4.2, AMQP 1.0
   only — unreachable from the current AMQP 0.9.1 client).
2. **Poison-handling collision**: RabbitMQ 4.0 defaults quorum `delivery-limit=20`; broker
   dead-letters/drops before Wolverine's error policies finish. NServiceBus explicitly manages
   this (their issue #1550). Wolverine needs a deliberate ownership decision + docs.
3. **Delayed delivery**: the delayed-message-exchange plugin (MassTransit's scheduler basis) is
   **unmaintained/dead** (Mnesia removed in 4.3). Options: NSB-style TTL+DLX delay-level
   topology (unbounded native delay, survives without a DB), or 4.3's native quorum delayed
   retry (retry backoff only). Wolverine's DB scheduler stays the durable default; a
   broker-native option is competitive parity.
4. Smaller: publisher confirms default **off** (`WolverineRabbitMqChannelOptions`); no priority
   queue helper (`x-max-priority`); no single-active-consumer helper; no consistent-hash
   exchange support; headers-exchange has no binding-argument fluent API; direct-reply-to unused
   for request/reply.

### Kafka

Already strong: retry topics with tiered delays (`MoveToKafkaRetryTopic`), commit modes, native
DLQ topic, Schema Registry Avro/JSON, static membership, cooperative sticky, broker-per-tenant,
`ProcessConcurrentlyByKey`.

Unexploited:
1. **Transactions / exactly-once** (consume-transform-produce, offsets in producer txn) — table
   stakes in Spring Kafka (EOSMode.V2), SmallRye, Silverback. Explicitly absent
   (`KafkaTransportExpression.cs:140`). Related, arguably better fit for Wolverine:
   **offsets-committed-in-application-DB** (Silverback/SmallRye checkpoint pattern) — Wolverine
   already owns a DB envelope store; committing offsets transactionally with app data gives
   effective exactly-once without Kafka transactions.
2. **KIP-848 next-gen rebalance** — available today in Confluent.Kafka 2.12+ via `GroupProtocol`
   config; cheap to expose/document/test.
3. **Share groups (KIP-932, GA in Kafka 4.2)** — per-message ack/nack/reject queue semantics.
   Spring Kafka 4.1 ships it first-class. **Blocked for .NET until Confluent ships share
   consumers (~H2 2026)** — track librdkafka; be first in .NET when it lands.
4. **Replay/seek surface** — no `seekToTimestamp`/offset-reset API on listeners (Spring's
   `ConsumerSeekAware`); tiered storage (3.9 GA) makes replay-from-history mainstream.
5. Smaller: Protobuf serdes missing (Avro/JSON only); no manual partition `Assign`;
   exception-type-routed DLTs (Spring Kafka 3.2) on top of the existing retry-topic machinery.

### Azure Service Bus

Already strong: sessions (basic), native scheduled enqueue, full CreateQueue/Subscription/Rule
options incl. SQL filters + reconciliation, native DLQ + recovery listener, sharded queues,
named brokers, MT/NSB interop.

Unexploited:
1. **PrefetchCount** — not surfaced at all; cheapest throughput win in the transport.
2. **Deferral** (`DeferMessageAsync` + receive-by-sequence) — native "park until ready";
   natural fit for out-of-order saga messages. No framework exposes it.
3. **Richer session support** — MT has `MaxConcurrentSessions`/`MaxConcurrentCallsPerSession`/
   session-id formatters; NSB has nothing → surface richly and it's a competitive stick.
   **Session state** (broker-hosted per-key blob) is unexploited by everyone but MT's saga repo.
4. **Cross-entity transactions** (send-via) — atomic settle+send, the broker-side outbox; NSB
   has it (`SendsAtomicWithReceive`), Wolverine doesn't.
5. **Scheduled-message cancellation** — ASB is the only major broker with native cancel;
   Wolverine maps `ScheduledEnqueueTime` but never the returned sequence number.
6. Untouched by anyone (greenfield): auto-forwarding topologies, Event Grid-triggered
   scale-to-zero listeners, Geo-Replication (data, GA 2025) awareness, dedup
   (`RequiresDuplicateDetection` + `Envelope.DeduplicationId`).
7. **Azure Event Hubs** — no Wolverine transport; MT covers it as a rider. Candidate new
   transport (partitions/consumer groups/checkpointing → pairs with the offsets-in-DB pattern).

### Amazon SQS / SNS

Already strong (SQS): FIFO groups + dedup, full attribute passthrough, native DLQ + recovery,
**fair queues already exposed** (`EnableFairQueueMessageGroups` — ahead of every competitor),
broker-per-tenant, CloudEvents/MT/NSB mappers.

Unexploited:
1. **Native `DelaySeconds`** (≤15 min) unused — `SupportsNativeScheduledSend => false`. Also the
   NSB pattern for **unbounded** native delay (FIFO staging queue + re-loop), and **EventBridge
   Scheduler** as a cancellable, timezone-aware scheduled-send backend (greenfield — nobody
   integrates it).
2. **Programmatic DLQ redrive** — `StartMessageMoveTask`/`ListMessageMoveTasks` as a one-call
   "replay dead letters" verb (progress + cancel). No framework wraps it; CritterWatch synergy.
3. **Payload limits stale**: SQS max is now 1 MiB (Aug 2025). No S3 claim-check story (no
   official .NET extended client — open niche, see cross-cutting #3).
4. **SNS**: payload-based filter policies (`FilterPolicyScope=MessageBody`) unclaimed by any
   framework; non-SQS subscription protocols (lambda, http, firehose…) unimplemented
   (`NotImplementedException`); `PurgeAsync`/`GetAttributesAsync` TODOs.
5. **EventBridge** (bus/rules/pipes/archive+replay) — candidate adjacent transport, uncontested.

### GCP Pub/Sub

Already strong: exactly-once flag, ordering keys (GroupId→OrderingKey), native DLQ + retry
policy, filters, flow control, project-per-tenant. Uncontested lane — deepen it.

Unexploited: **snapshots + seek** (replay/rewind, and seek-to-now as a proper `PurgeAsync` —
current purge pulls/acks max 50 msgs); push subscriptions (`PushConfig`); topic schemas
(Avro/proto); BigQuery/Cloud Storage export subscriptions (provision from Wolverine config);
Single Message Transforms (GA 2025); `GetAttributesAsync` returns empty.

### NATS

Already strong: core + JetStream endpoints, full StreamConfiguration provisioning, DeliverPolicy,
queue groups, native scheduled send gated on server 2.12+, DLQ subjects, tenancy via connection
or subject prefix.

Unexploited (biggest untapped pool of any transport):
1. **JetStream KV store** — OCC/CAS via revision numbers → saga storage, dedup tables,
   distributed locks, leader election (Rebus.Nats prior art); **Object store** → claim-check
   backend. NATS.Net fully supports both; Wolverine uses neither.
2. **2.11 pull-consumer priority groups** — `pinned_client` = broker-native exclusive consumer
   with automatic failover (relevant to GlobalPartitioning native mode and to Wolverine's
   exclusive-listener choreography); overflow groups = standby consumers.
3. **Consumer pausing** (`PauseUntil`) — maps directly onto Wolverine's pause-listener ops.
4. **`Nats-Msg-Id` dedup + double-ack** — broker-side idempotent publish keyed by envelope ID
   (Watermill's exactly-once recipe); free outbox-style dedup.
5. **2.12 atomic batch publish** — all-or-nothing multi-message publish; a broker-side
   transactional outbox primitive. **2.14 recurring cron schedules** (`@every`/crontab in
   `Nats-Schedule`) — broker-native recurring messages.
6. **Deterministic subject mapping** `{{partition(N,...)}}` + Orbit pcgroups — native
   partitioning (GlobalPartitioning v2 candidate).
7. Smaller: AckPolicy fixed at Explicit (AckAll is a cheap batch-ack win for ordered inline
   listeners); mirrors/sources (tenant fan-in, DR); accounts-based tenancy; capture
   `MAX_DELIVERIES` advisories into the DLQ stream; micro/service API for handler observability.

### Redis Streams

Already strong: consumer groups, XAUTOCLAIM recovery loop, native DLQ stream, sorted-set
scheduled/retry, broker-per-tenant. Already the best .NET Redis transport (SlimMessageBus is
at-most-once lists/pub-sub); Watermill is the cross-language model and Wolverine matches its
core loop.

Unexploited: **Redis 8.2 `XACKDEL`/`XDELEX`** (atomic ack+delete work-queue semantics, kills the
ack-then-trim race — needs raw `Execute` until SE.Redis surfaces it); `MAXLEN`/`MINID` trimming
policy (unbounded stream growth today; purge = full `KeyDelete`); consumer-group lag metrics
(`XINFO GROUPS`) for CritterWatch; sharded pub/sub or keyspace notifications as a low-latency
wakeup to shorten the polling gap (SE.Redis forbids blocking reads by design — polling is
structural; a dedicated-connection option is the escape hatch).

### Pulsar

Already strong: all four subscription types incl. KeyShared, native DLQ + retry-letter topics
with tiered delays, native redelivery, ack strategies, schemas + producer dedup, regex
subscriptions, hot-tail readers. No major .NET competitor has a Pulsar transport at all.

Unexploited:
1. **Native delayed delivery (`DeliverAt`/`DeliverAfter`)** — DotPulsar supports it;
   Wolverine doesn't map it to `SupportsNativeScheduledSend`. Cheapest headline win in the
   whole analysis: Pulsar is *the* broker with true arbitrary-timestamp scheduling.
2. Client-blocked by DotPulsar (broker-native but unreachable): transactions, negative acks +
   backoff, batch-index ack, chunking, TableView. Options: contribute upstream, emulate
   (retry-letter + delay already emulates nack-with-backoff), or evaluate the F#
   `pulsar-client-dotnet` which covers all of these.
3. Smaller: partitioned-topic admin (create/expand partitions); Pulsar-native tenant/namespace
   mapping for Wolverine tenancy (currently tenants = separate clusters only).

### MQTT

Current state: v5 protocol forced, QoS + retain per topic, managed client passthrough, JWT
re-auth, broker-per-tenant. Benchmark competitor: Silverback (outbox, dedup/exactly-once,
chunking, batching, in-memory mock).

Unexploited (all MQTT 5, all supported by MQTTnet): **user properties** = real headers (today
interop requires envelope serialization); **response topic + correlation data** = protocol-level
request/reply mapping onto Wolverine's reply semantics (Spring Integration precedent);
**message expiry** (→ `DeliverBy`); **shared subscriptions** `$share/{group}/{topic}` =
competing consumers (MQTTnet has no first-class API but raw filter subscription works —
verify per broker); session-expiry control; LWT helper (presence/AgentState signal to
CritterWatch); EMQX `$delayed/{seconds}/` native delayed publish as an opt-in scheduling
backend; HiveMQ `$dead/$dropped` feeds as a DLQ source.

### PostgreSQL / SQL Server queues

Current state: durable named queues + scheduled tables, sticky per-node listeners, FIFO,
MT+NSB interop transports (Postgres), NSB only (SQL Server), DB-per-tenant.

Unexploited:
1. **Postgres `LISTEN`/`NOTIFY`** — dequeue is poll-only; latency floor = `PollingInterval`.
   MassTransit's SQL transport (their strategic v9 bet) uses LISTEN/NOTIFY. Matching this makes
   the "Postgres is my broker" story fully competitive. SQL Server analogue: Service Broker /
   `SqlDependency` (heavier; evaluate, don't promise).
2. **MassTransit interop asymmetry** — Postgres has MT+NSB transports, SQL Server NSB only.
3. **GlobalPartitioning** — missing on both; Wave 2 of the rollout plan (best candidates).

### SignalR

Hub integration, not a broker. Gaps that matter: Azure SignalR package referenced but no
service-mode features wired; no client-result invocations; no streaming hub methods. Best
strategic use: the push channel for **subscription queries** (cross-cutting #9).

---

## Cross-cutting candidate features (ranked)

**T** = table stakes (2+ competitor frameworks have it), **U** = unique differentiator.

1. **DLQ depth cluster (U/T)** — the highest-leverage cluster; builds on Wolverine's existing DB
   dead-letter store + GlobalPartitioning GroupId, and nobody in .NET has Axon-grade DLQ:
   - Order-preserving DLQ: same-GroupId messages queue behind a dead-lettered one (Axon
     `SequencedDeadLetterQueue` is the only prior art) (U).
   - Exception-type-routed dead-letter destinations (Spring Kafka 3.2) (U in .NET).
   - Standardized diagnostic headers on every DLQ move: attempt count, original
     endpoint/topic/partition/offset, exception FQCN + stacktrace (T, cheap).
   - Redrive verbs: wrap SQS `StartMessageMoveTask`; a uniform `IDeadLetterAdmin` "replay"
     across transports; ServicePulse-style UX belongs to CritterWatch (T).
2. **Broker-native scheduling backends (T)** — keep the DB scheduler as durable default, add
   opt-in native delay per transport: ASB scheduled(+cancel, already mapped minus cancel),
   Pulsar `DeliverAt` (cheapest win), NATS 2.12 (done), SQS DelaySeconds/FIFO-loop/EventBridge
   Scheduler, Rabbit delay-level topology, EMQX `$delayed`. NSB treats native delay as the
   default everywhere; MT exposes it per transport.
3. **Claim check / large-message offload (T)** — MT, NSB, Brighter, SlimMessageBus all have one;
   Wolverine has none. One middleware + pluggable stores (S3, Azure Blob, GCS, **NATS Object
   store**, Marten/DB LOB). SQS 1 MiB / ASB 100 MB limits make this concrete. Silverback-style
   chunking is the alternative for Kafka/MQTT.
4. **Kafka exactly-once cluster (T)** — offsets-in-application-DB commit strategy first (unique
   fit with Wolverine's envelope storage), Kafka transactions second, share groups when the
   .NET client lands (~H2 2026).
5. **Replay/seek surface (T)** — a uniform "rewind this listener" API: Kafka offsets/timestamp,
   Pulsar reader/seek, JetStream DeliverPolicy, Pub/Sub snapshots+seek, Rabbit stream offsets.
   All five brokers now support it natively; no .NET framework unifies it.
6. **AsyncAPI export (T)** — FastStream/SlimMessageBus prior art; Wolverine already introspects
   its full endpoint/handler graph (`describe`, OpenAPI precedent) — natural extension, cheap,
   very visible.
7. **Listener runtime controls (T)** — pause/resume (NATS `PauseUntil` native; Kafka
   pause-without-rebalance; others via agent stop), rate limiting per endpoint (MT, Broadway,
   Watermill, Celery), pause-instead-of-sleep during error backoff.
8. **Dedup middleware (T)** — time-window ID dedup distinct from the durable inbox: surface ASB
   `RequiresDuplicateDetection`, NATS `Nats-Msg-Id`, SQS FIFO dedup, Pulsar producer dedup under
   one `Envelope.DeduplicationId` story (partially wired today: SQS + Pulsar only).
9. **Subscription queries over SignalR (U)** — Axon-only feature; query returns current result +
   live updates pushed as projections/handlers update. Wolverine uniquely has the pieces
   (HTTP + SignalR transport + Marten projections). Strong critter-stack demo material.
10. **Batch refinements (T)** — batch-key routing (Broadway `put_batch_key`), byte-based batch
    sizing, per-message failure inside a batch (Spring `BatchListenerFailedException` pattern).
11. **Second-level failure handlers (T)** — dispatch exhausted messages as `IFailed<T>` to a
    compensation handler (Rebus/Broadway/Axon) as an alternative to dead-lettering.
12. **New transports (U)** — Azure Event Hubs (MT rider exists; pairs with #4 checkpointing) and
    EventBridge (rules/scheduler/pipes; uncontested). Evaluate demand before committing.

Already covered, no action: in-proc per-key serialized execution (local partitioned topologies +
`ProcessConcurrentlyByKey`), SQS fair queues (shipped), retry topics on Kafka/Pulsar (shipped),
test harness (TrackedSession covers the core; per-transport in-memory stubbing is the only delta),
distributed jobs (sagas + scheduled messages; revisit only if demand), routing slips (sagas).

## Suggested sequencing

| Phase | Items | Notes |
|---|---|---|
| Quick wins (each ≤ a few days) | Pulsar `DeliverAt` native scheduling; ASB `PrefetchCount`; SQS `DelaySeconds`; Kafka KIP-848 doc/expose; DLQ diagnostic headers; ASB scheduled-cancel | Independent, release-note friendly |
| GlobalPartitioning plan Waves 1–2 | docs/tests catch-up + Postgres/SQL Server sharded queues | see `GLOBAL-PARTITIONING-ROLLOUT-PLAN.md` |
| Feature cluster 1 | DLQ depth (order-preserving, exception routing, redrive verbs) | biggest differentiation-per-effort |
| Feature cluster 2 | Claim check middleware + stores | closes the most-cited table-stakes gap |
| Feature cluster 3 | Native scheduling backends (Rabbit delay topology, EventBridge Scheduler, EMQX) | per-transport opt-ins |
| Deep lanes | NATS KV/ObjStore + priority groups; Rabbit super streams; Kafka offsets-in-DB; Pub/Sub seek/snapshots; MQTT v5 properties | one epic per transport |
| Watch list | Kafka share groups (.NET client ~H2 2026); MQTTnet shared-subscription API; SE.Redis XACKDEL surfacing; DotPulsar transactions/nack | re-check quarterly |
