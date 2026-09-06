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
