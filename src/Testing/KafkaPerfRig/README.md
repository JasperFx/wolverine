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

Measured findings and the experiment ledgers live in the deep-dive issues (GH-3490/3492/3493/
3494, all closed with their ledgers) and the `*-PERF-DEEP-DIVE-PLAN.md` documents at the repo
root (session artifacts).
