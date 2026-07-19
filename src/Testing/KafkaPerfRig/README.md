# KafkaPerfRig

Load-test rig for [GH-3490](https://github.com/JasperFx/wolverine/issues/3490) — the Kafka
performance deep dive. Deliberately **not** part of either `.slnx` solution or CI.

Two twin harnesses generate identical traffic and share one monotonic stage clock
(`Stopwatch.GetTimestamp`, valid across processes on one box):

- **wolverine**: `WolverinePublisher` / `WolverineConsumer` (`UseKafka`, configurable endpoint
  modes, batching, sequencing)
- **native**: raw Confluent.Kafka producer + sequential consumer with the same
  store-after-process offset semantics

Stages recorded per message: `t0` publish call → `t2` consume return/envelope mapping →
`t3` handler entry → `t4` handler exit. Results land as raw CSV + p50/p95/p99 JSON.

## Running

```bash
docker compose up -d kafka postgresql   # from the repo root

./rig.sh wolverine baseline             # one scenario (client-shaped defaults)
./rig.sh native native-anchor           # the native twin
./cells.sh                              # the full experiment sweep
./cells.sh baseline send-inline         # selected cells only
```

Scenario knobs are `RIG_*` env vars — see `RigConfig.cs`. The defaults reproduce the
GH-3490 report shape: 1Kb flow @ 8/s + 100Kb flow @ 0.6/s, buffered listeners, sender
batching (10, 10ms), per-game semaphore sequencing, ~9ms simulated handler.

Measured findings and the experiment ledger live in the issue and
`KAFKA-PERF-DEEP-DIVE-PLAN.md` (repo root, session artifact).
