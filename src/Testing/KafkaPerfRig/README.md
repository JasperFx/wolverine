# KafkaPerfRig

Load-test rig for the transport performance deep dives — GH-3490 (Kafka), GH-3492 (RabbitMQ),
GH-4026/GH-4039 (Kafka topic groups, NATS JetStream, Pulsar). Despite the name it is now a
multi-transport rig. Deliberately **not** part of either `.slnx` solution or CI.

Harnesses generate identical traffic (shared corpus, rate loops, and recorder) and share one
monotonic stage clock (`Stopwatch.GetTimestamp`, valid across processes on one box):

| Roles | Harness | Notes |
|---|---|---|
| `wolverine-{publisher,consumer}` | Kafka (`UseKafka`) | plus a **native** twin: raw Confluent.Kafka (`native-{publisher,consumer}`) |
| `rabbit-{publisher,consumer}` | RabbitMQ | plus a native RabbitMQ.Client twin (`native-rabbit-*`) |
| `nats-{publisher,consumer}` | NATS JetStream | deletes its per-run stream on shutdown |
| `pulsar-{publisher,consumer}` | Pulsar | per-run topics deleted via the admin API in `rig.sh` |
| `redis-{publisher,consumer}` | Redis Streams | per-run stream keys, deleted by the consumer on shutdown |
| `asb-{publisher,consumer}` | Azure Service Bus | emulator or real namespace via `RIG_ASB`; queues NOT torn down (see below) |

Stages recorded per message: `t0` publish call → `t2` consume return/envelope mapping →
`t3` handler entry → `t4` handler exit. Results land as raw CSV + p50/p95/p99 JSON.

## Running

```bash
docker compose up -d kafka rabbitmq nats pulsar postgresql   # whichever brokers the cells need

./rig.sh wolverine baseline             # one scenario (client-shaped defaults)
./rig.sh native native-anchor           # the native twin
./cells.sh                              # Kafka sweep (incl. max-durable-group cells)
./cells-rabbit.sh r-max-durable         # RabbitMQ cells, selected
./cells-nats.sh                         # NATS JetStream cells
./cells-pulsar.sh                       # Pulsar cells
```

Scenario knobs are `RIG_*` env vars — see `RigConfig.cs`. The defaults reproduce the
GH-3490 report shape: 1Kb flow @ 8/s + 100Kb flow @ 0.6/s, buffered listeners, sender
batching (10, 10ms), per-game semaphore sequencing, ~9ms simulated handler. Notable later
additions: `RIG_KAFKA_TOPIC_GROUP=1` (one `ListenToKafkaTopics` consumer over both topics),
`RIG_MAX_RECEIVE=n` (pin `MaximumMessagesToReceive`; `1` reproduces pre-batching behavior
inside the same build), `RIG_PUBLISHERS=n` (concurrent max-throughput publish loops — one
awaited DotPulsar produce is ~2ms, so a single loop publisher-bounds every Pulsar cell at
~450/s), `RIG_LOG_LISTENER=1` (surface back-pressure pause/restart logs, which are
Information-level and otherwise filtered).

## Hygiene — believe no number without it

Max-throughput cells publish millions of messages per run and the consumer keeps only a
fraction; the leftovers poison every later run:

- `rig.sh` **drops the rig's Postgres schema before each run** — 6M leftover inbox rows once
  made Wolverine's startup recovery time out, so consumer hosts failed to start and cells
  "measured" a dead host.
- Per-run Kafka topics / Rabbit queues / Pulsar topics are deleted by `rig.sh`; the NATS
  consumer deletes its own per-run stream (67M messages / 101 GB had accumulated in the broker
  container in one afternoon).
- Build with `MSBUILDDISABLENODEREUSE=1`: a day of builds leaves hundreds of idle MSBuild
  nodes holding tens of GB, which starves the brokers and skews cells.
- **A/B after the change is committed needs two worktrees built independently.** `git stash
  push` of already-committed paths exits 0 with nothing stashed — a stash-based A/B then
  silently measures fix-vs-fix.

### Redis (GH-4329) — measured 2026-09-06

`RIG_MAX_RECEIVE` maps to the Redis listener's `BatchSize`, which *is* the XREADGROUP COUNT and
is exactly what the GH-4329 batched dispatch keys off (`streamResults.Length > 1`). So `=1`
reproduces the pre-change one-at-a-time path **inside the same build** — a far stronger A/B than
two worktrees.

| durable cell | r1 | r2 | mean |
|---|---|---|---|
| `RIG_MAX_RECEIVE=1` (pre-GH-4329) | 1,059/s | 1,055/s | **1,057/s** |
| default batch (post-GH-4329) | 2,744/s | 2,729/s | **2,737/s** |

**+159% (2.6x)**, rounds within 0.6% of each other. The durable Redis endpoint had been paying one
inbox-insert round trip per message.

### Outbox store, per store (GH-4369) — measured 2026-09-06

Role `outbox-store`, selected with `RIG_STORE`. Not an end-to-end cell on purpose: GH-4369 changes
exactly one method on three stores, so the honest instrument is that method against the real
database. An end-to-end cell would bury a per-store round-trip change under broker time.

Each round stores a fresh batch of `RIG_STORE_BATCH` (default 100) envelopes twice — once as N calls
to the single-envelope overload (the pre-GH-4369 shape, and still the default-interface behaviour for
any store that does not override the batch), once as one call to the batch overload. **The two arms
alternate order round to round** so neither systematically owns the cold cache, each arm builds its
own envelopes so neither is re-writing rows the other wrote, and the outgoing table is cleared
between rounds so a steadily growing table cannot flatter whichever arm runs first. 20 rounds after
5 warmup rounds.

| store | sequential p50 | batched p50 | speedup |
|---|---|---|---|
| PostgreSQL — the control, batched since GH-4319 | 57.30ms | 2.46ms | **23.3x** |
| Oracle — new `INSERT ALL` | 49.75ms | 3.81ms | **13.1x** |
| RavenDB — new one-session batch | 348.81ms | 5.69ms | **61.4x** |

PostgreSQL is in the table as a **control, not a result**: it already had the batch override, so it
says the harness measures what it claims to. RavenDB is the largest because the un-batched path
opened its own `IAsyncDocumentSession` — and therefore its own HTTP round trip — per envelope.

**Cosmos DB is deliberately absent.** The only Cosmos here is the emulator, and GH-4331 above is the
standing lesson about quoting a number an emulator produced. Its `TransactionalBatch` implementation
is verified by the shared `MessageStoreCompliance` suite; it is not given a throughput claim.

Watch the machine before trusting a round here. These runs had eight orphaned Cosmos emulator
containers resident, which is visible in the p95 spread (PostgreSQL sequential p95 319ms against a
57ms p50) even though the p50 gap is far too large to be noise.

### Fixed-arity batched inserts (GH-4320) — measured 2026-09-06: premise REFUTED, change kept

Same `outbox-store` role, with two additions: an in-build **legacy-shape arm** that runs
`DatabasePersistence.BuildOutgoingStorageCommand` (the per-envelope values-clause form) against the
same database in the same process, and `RIG_VARY_BATCH=1`.

`RIG_VARY_BATCH` exists because of a flaw found in this harness while using it. **A constant batch
size cannot test a plan-cache claim at all**: at a fixed size even the per-envelope form has stable
command text and is auto-prepared like anything else. The defect GH-4320 described is that the text
varies *with* the batch size — and a coalescer produces varying sizes by construction. The flag walks
the size from 2 to 100 on a fixed seed so both arms see the same sequence.

| cell (varying batch size, legacy → fixed arity) | r1 | r2 |
|---|---|---|
| `Max Auto Prepare` unset — **Wolverine's default** | 1.318 → 0.988ms, **1.33x** | 1.057 → 0.771ms, **1.37x** |
| `Max Auto Prepare=20` | 1.100 → 1.138ms, 0.97x | 1.179 → 0.987ms, 1.19x |

**The change is a consistent ~1.35x on the batched insert, and the reason GH-4320 gave for it is
wrong.** Two things sank the plan-cache premise. Wolverine never sets `Max Auto Prepare` and Npgsql
defaults it to 0, so no Wolverine command was being auto-prepared either way. And when it *is* switched
on, the fixed-arity advantage disappears into noise rather than growing — prepared-statement reuse was
never where the cost was. The saving is the smaller command and the parameter count: binding 900
parameters client-side and parsing them server-side is more work than binding 9.

Kept anyway, on the measurement rather than the theory. The structural benefit that holds regardless:
parameter count no longer scales with batch size, so a batch cannot walk towards a provider's
parameter ceiling.

⚠️ **Do not compare absolute numbers across sessions here.** The same sequential arm measured 57.30ms
with eight orphaned emulator containers resident and 29.6ms once they were gone. That is why the legacy
arm is built into the run.

### Durable local queue (GH-4319) — measured 2026-09-06

The only lane with no broker in it: one process publishes into a durable local queue backed by
PostgreSQL and handles the messages itself. That isolates exactly what GH-4319 changes —
`DurableLocalQueue.StoreAndForwardAsync`'s inbox `INSERT`, which used to open its own pooled
connection for one row on every publish.

`RIG_STORE_BATCH=1` sets `StoreIncomingBatchSize` to 1, which is the pre-GH-4319 one-INSERT-per-publish
path **inside the same build**. Run it as `dotnet run -c Release -- local-queue`.

**Coalescing only happens where the application publishes concurrently.** There is no timer, so a
single awaited publish loop never has two writes in flight and forms no batches at all — it pays
nothing and gains nothing. The saturated cell therefore runs `RIG_PUBLISHERS=16`, which is the shape
a server handling concurrent requests actually has. A rig cell at `RIG_PUBLISHERS=1` would measure
this change as a null result, correctly and uselessly.

Two cells, two rounds each, interleaved, `RIG_HANDLER_MS=0 RIG_SEQ=none`:

**Saturated** (`RIG_SMALL_RATE=-1 RIG_PUBLISHERS=16`, 15s warmup + 45s):

| cell | r1 | r2 | mean | publish p50 | publish p99 |
|---|---|---|---|---|---|
| `RIG_STORE_BATCH=1` (pre-GH-4319) | 7,297/s | 7,413/s | **7,355/s** | 2.29ms | 3.69ms |
| default batch 100 (post-GH-4319) | 9,156/s | 9,164/s | **9,160/s** | **1.70ms** | **2.71ms** |

**+24.5% throughput, and latency went DOWN 26% at p50 / 27% at p99.** Round spread inside each arm
is 1.6% and 0.1%, well under the 24% gap. The latency direction is the point, not a bonus: a
time-windowed coalescer here would have moved p50 the other way, which is precisely the shape
GH-3490 measured at a 5,767ms transit p50. Batching that forms only from concurrency takes pressure
off the connection pool instead of adding delay.

**Trickle** (`RIG_SMALL_RATE=8 RIG_LARGE_RATE=0.6`, 20s warmup + 60s) — the safety cell:

| cell | r1 publish p50 | r2 publish p50 | mean |
|---|---|---|---|
| `RIG_STORE_BATCH=1` (pre-GH-4319) | 2.091ms | 2.057ms | **2.07ms** |
| default batch 100 (post-GH-4319) | 2.094ms | 2.130ms | **2.11ms** |

**No measurable change**, and that is the result being looked for: at 8/s the publishes are ~125ms
apart, nothing is ever concurrent, every write goes straight down the per-envelope path. The 0.04ms
gap is inside the round-to-round spread of the *before* arm alone (0.034ms). Throughput is identical
by construction — both arms are rate-controlled and handled all 640 messages.

Measure both cells for any future change here. Throughput alone cannot tell a coalescer that helps
under load from one that quietly taxes a lone publish.

### Azure Service Bus (GH-4331) — measured 2026-09-06: NULL RESULT → **change reverted**

`RIG_ASB_PREFETCH=-1` sets an explicit transport-wide 0, which by design still beat the computed
per-mode default — that was the pre-GH-4331 shape, again inside one build. Preferred over two
worktrees here because the emulator's throughput drifts across container restarts.

The emulator caps around 50 queues and wedges if its objects are deleted underneath it, so this
lane deliberately does **not** tear down its per-run queues the way Redis and NATS do. Restart the
emulator between long sweeps instead.

| buffered cell | r1 | r2 | mean |
|---|---|---|---|
| `RIG_ASB_PREFETCH=-1` (pre-GH-4331) | 34.9/s | 35.1/s | **35.0/s** |
| computed default (post-GH-4331) | 34.7/s | 31.1/s | **32.9/s** |

**No benefit detected.** The post-change arm is marginally lower and its own spread (~11%) exceeds
the gap. Read this as "the cell cannot see it", not "the change is worthless": the emulator tops
out near 35 msg/s with near-zero round-trip latency, and prefetch exists precisely to hide network
latency. Testing it properly needs a real Azure namespace. **Do not quote a throughput number for
GH-4331.**

**Outcome: the change was reverted.** No measured benefit, and prefetch is not free — a buffered
message ages against its Azure Service Bus lock from the moment the *client* holds it, with no
Envelope yet and therefore no `LeaseRenewalTracker` renewal. A default that trades real lock
exposure for an unmeasured gain does not earn its place. The lane and the knob stay; the knob's
polarity flipped. `RIG_ASB_PREFETCH=0` (the default) is now the shipping shape, and a **positive**
value reproduces what GH-4331 proposed — `RIG_ASB_PREFETCH=20` is one receive batch. Re-run this
against a real namespace to settle the issue.

GH-4331 also shipped with a red unit test: `native_ack_prefetch_defaults_4051.every_other_mode_
keeps_the_shipping_default_of_zero` asserts 0 for Buffered/Durable/Inline and was never updated,
so `main` carried a failing Azure Service Bus unit test from #4351 until the revert. The lesson is
in the ledger's own ground rules — run the covering lane, not just the one you were thinking about.

The emulator also needs BOTH connection strings — AMQP on 5673 and management on 5300 — because
AutoProvision goes through the management API. `UseAzureServiceBusEmulator(amqp, management)` is
the wiring; `UseAzureServiceBus(amqp)` alone fails at startup with an HTTP connect error.

Measured findings and the experiment ledgers live in the deep-dive issues (GH-3490/3492/3493/
3494, all closed with their ledgers) and the `*-PERF-DEEP-DIVE-PLAN.md` documents at the repo
root (session artifacts).
