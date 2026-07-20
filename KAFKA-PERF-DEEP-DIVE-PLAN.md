# Kafka Performance Deep-Dive Plan (2026-07-18)

Goal: explain and then shrink the gap a client measured between "native Confluent.Kafka" and
Wolverine-over-Kafka (p50 transit 7.9ms vs 25.8ms on 1Kb; 8.4ms vs 102.4ms on 100Kb; execution
20.1ms vs native handler 8.9ms), build a reusable load harness + dotTrace protocol, produce
throughput-tuning guidance, and evaluate a "single-message-type fast-path listener"
optimization. Companion docs: `TRANSPORT-CAPABILITY-RESEARCH-2026-07-18.md`,
`GLOBAL-PARTITIONING-ROLLOUT-PLAN.md`.

All file:line cites verified against `main` @ 6.20.0.

---

## 0. What Wolverine's numbers actually measure (read this before comparing anything)

The client is comparing their own native-harness timestamps against Wolverine's metrics. These
are **not the same intervals**, and two of the deltas may be partly definitional:

- **`wolverine-execution-time`** (`MetricsConstants.cs:6`): monotonic Stopwatch, per handler
  *attempt*. Starts at the top of `Executor.ExecuteAsync` (`Executor.cs:219-223`) — i.e. AFTER
  queue dwell and AFTER deserialization — stops right after `Handler.HandleAsync` returns
  (`Executor.cs:244`). Includes: middleware, the codegen chain, per-message CTS setup, and —
  critically — **any time spent blocked inside custom middleware** (see T2 below). Excludes:
  deserialization, cascading-message flush (listener path — `MessageSucceededContinuation.cs:22-26`
  flushes *after* `ExecutionFinished`), transport ack, inbox mark-handled.
  **Gotcha:** sub-1ms samples are dropped entirely (`WolverineRuntime.Tracking.cs:130` —
  `if (time > 0)` after `(long)` ms truncation), so the histogram is biased upward for fast
  handlers.
- **`wolverine-effective-time`** (`MetricsConstants.cs:10`): wall clock, `UtcNow − envelope.SentAt`
  recorded at `MessageSucceeded` (`Tracking.cs:146-165`) — *after* cascading flush AND after
  `CompleteAsync()` (transport ack / durable mark-handled UPDATE). So effective time =
  producer-side stamp → broker transit → consumer dwell → (durable: inbox INSERT) → queue dwell →
  deserialize → handler → cascading flush → ack. It is **cross-machine wall clock**: skew between
  service A/B and C shifts every sample. If a `sent-at` header is ever missing/unparseable the
  sample is ~year-1-based garbage (`EnvelopeMapper.cs:531-542` returns `default`), and raw-JSON
  Kafka endpoints take SentAt from the **broker record timestamp** instead
  (`IKafkaEnvelopeMapper.cs:85-88`).
- Wolverine already has the decomposition the comparison needs, opt-in:
  `wolverine.envelope.transport_lag_ms` and `receive_dwell_ms` Activity tags
  (`WolverineTracing.cs:238-249,324-344`, via `Tracking.HandlerExecutionDiagnosticsEnabled`).

**Baseline rule for every experiment below:** measure with our own histogram capture at fixed
stages (see H1) rather than trusting either side's "effective" number; record Wolverine's
metrics *alongside* so we can also explain the client's dashboards to them.

---

## 1. Theories of overhead, ranked (pre-registered hypotheses)

Each theory gets an experiment tag (E#) in the matrix in §4. Prediction written down before
running = honest science.

### T1 — Sender-side batch debounce dominates "transit" (HIGH confidence, explains p50 transit gap)
`BatchedSender` is the default for Buffered/Durable Kafka subscribers (`KafkaTopic.cs:302-330`).
Its `BatchingChannel` timer is a **debounce, not a fixed-interval flush**: every posted message
resets the timer to the full `MessageBatchTimeout`
(JasperFx `BatchingChannel.cs:97-122`). Consequences:
- A lone message waits the *full* timeout before hitting the wire.
- A steady trickle arriving faster than the timeout keeps postponing the flush until
  `MessageBatchSize` accumulates.

Defaults are **100 / 250ms** (`Endpoint.cs:275,288`) — the client's "batch size = 10, batch
timeout = 10ms" is **not a Wolverine default anywhere**; they configured it (or it's another
framework's number — verify with them, §7). With 10/10ms and a steady match-feed stream, p50
transit +10-18ms vs native fire-and-forget `ProduceAsync` is exactly what this mechanism
predicts for the 1Kb case (25.8 vs 7.9). For the 100Kb flow (~0.6 msg/s), every message is a
"lone message" → full timeout + a second delivery-side hop, consistent with the much larger
102 vs 8.4 gap if their timeout there was larger (or still 250ms default on that endpoint —
worth asking).
Also in this family: `MessageBatchMaxDegreeOfParallelism` default **1** (`Endpoint.cs:281`) —
one batch in flight at a time per endpoint.
**Prediction:** `MessageBatchSize(1)`/tiny timeout or Inline-mode sending (minus T5's flush bug)
collapses most of the transit gap; `linger.ms`-style tuning on the native side is what Wolverine's
batching is duplicating badly.

### T2 — Their sequential-by-key semaphore middleware inflates "execution time" (HIGH confidence)
Execution time starts before middleware runs. A "sequential by partition key" semaphore
middleware **waits inside the measured window** — same-stream convoying is billed as handler
execution. Their native consumer achieves per-stream sequencing structurally (partition
assignment), so its "handler" number contains no wait. 20.1ms vs 8.9ms p50, and especially
235.7 vs 51.4 p99, smells like convoy wait + worker-slot occupation: a blocked semaphore
holds one of the `MaxDegreeOfParallelism` Block workers (default `max(ProcessorCount,5)`,
`Endpoint.cs:212`; `BufferedReceiver.cs:62-64`), so unrelated streams queue behind it →
head-of-line blocking that ALSO inflates dwell/effective tails.
**Prediction:** replacing the semaphore middleware with Wolverine's own
`PartitionProcessingByGroupId(...)` (`ListenerConfiguration.cs:110-113`, `ShardedExecutionBlock`
with per-slot serial execution, `ShardedExecutionBlock.cs:7`) moves the wait *out* of the
execution metric and removes worker-slot occupation; execution-time converges toward native
handler time.

### T3 — Buffered/queue dwell + back-pressure oscillation drives p95/p99 (MEDIUM-HIGH)
Service C runs no durable inbox → Buffered mode. Two mechanisms:
1. Queue dwell in the 10k-capacity worker channel is invisible but fully inside effective time.
2. `BackPressureAgent` (2s poll, `BackPressureAgent.cs:29-70`) stops the listener above
   `BufferingLimits.Maximum` = **1000** queued and restarts under **500** (`Endpoint.cs:259`).
   For Kafka, "stop" means `_consumer.Close()` — **leaving the consumer group → rebalance** —
   and restart re-joins → another rebalance (`ListeningAgent.cs:451-492`, `KafkaListener.cs:167-185`).
   Under sustained load this can oscillate every few seconds; each cycle = multi-second partition
   stall showing up as periodic effective-time spikes while execution time stays flat.
**Prediction:** dwell-stage histograms show sawtooth; raising `BufferingLimits` (or lowering
`MaxDegreeOfParallelism` contention per T2) flattens p99. Log listener stop/restart events during
the run to correlate.

### T4 — Durable mode: per-message inbox INSERT gates the consume loop (HIGH, for the durable leg)
Kafka delivers one envelope at a time, so `DurableReceiver` always takes the single-envelope
path: `StoreIncomingAsync(envelope)` = one connection + one INSERT **inline on the consume loop**
before the next `Consume()` (`DurableReceiver.cs:415-520` → `MessageDatabase.Incoming.cs:142-172`).
The batched multi-VALUES insert path exists (`ProcessReceivedMessagesAsync`,
`DurableReceiver.cs:608-668`; `MessageDatabase.Incoming.cs:174-197`) but **Kafka never uses it**.
Then success pays a second DB round trip (mark-handled UPDATE runs inline on the handler thread
*before* effective time is recorded — `DurableReceiver.cs:196-215`, RetryBlock first-attempt-inline).
Durable throughput ceiling ≈ 1/insert-latency per listener. This is both the main measurement
and the main optimization target for durable Kafka (O1 below).

### T5 — `InlineKafkaSender` blocking `Flush()` per message (HIGH confidence, Inline-mode only)
`InlineKafkaSender.SendAsync`: `await ProduceAsync(...)` then **synchronous `_producer.Flush()`
after every single message** (`InlineKafkaSender.cs:48-55`). Defeats librdkafka's linger/batching
entirely, blocks a thread, and applies to ALL broker-per-tenant sends (`KafkaTopic.cs:315-324`)
and the native DLQ sender. If any of the client's publish endpoints are Inline, this is
first-order. Independent of measurement: this looks like a straight bug to fix (O2).

### T6 — Envelope-mapper + resolution-chain per-message costs (Jeremy's theory; MEDIUM for
latency, HIGH for CPU/allocation at scale — quantify via microbench)
Per received message, warm path:
- **Mapper**: full header enumeration UTF8-decoded into a fresh `Dictionary`
  (`KafkaEnvelopeMapper.cs:32-40` + `Envelope.cs:55-64`), then **19 typed readers each do a
  reverse linear scan of the Kafka header list and re-decode the same bytes**
  (`EnvelopeMapper.cs:88-111,273-328`) — double decode of every reserved header — plus 2×
  `Guid.TryParse`, 3× `DateTimeOffset.TryParseExact`, 2× bool, 1× int, 1× `new Uri`, 1×
  `string.Split` (`EnvelopeMapper.cs:413-418,531-556`). Outgoing twin allocates
  `_envelopeToHeader.Values.ToArray()` **per message** (`EnvelopeMapper.cs:391-397`) and writes
  ~18 UTF8 headers.
- **Resolution chain**: 2× string-keyed `ImHashMap` message-type lookups (one hiding inside the
  `RequiresEncryption` check that runs even with no encryption configured —
  `HandlerPipeline.cs:190,286-300` + `WolverineOptions.Encryption.cs:96-100`), 1× content-type
  serializer lookup (`EnvelopeMapper.cs:119` → `Endpoint.cs:593-613`), 1× Type-keyed executor
  lookup (`HandlerPipeline.cs:334`), 1× reverse `ToMessageTypeName` lookup in the
  `Envelope.Message` setter (`Envelope.cs:225-233`), plus a wasted `NewId.NextSequentialGuid`
  per receive that the wire `id` header immediately clobbers (`KafkaTransportExtensions.cs:252`).
- **Executor fixed cost**: 2 CTS allocations + linked-token timer registration per message
  (`Executor.cs:227-228`), and the **"Successfully processed" log at Information level by
  default** (`HandlerChain.cs:177`).

Individually these are sub-microsecond to tens-of-microseconds; at 60k+ msg/run they are CPU,
allocation pressure, and GC — likely a second-order latency contributor but the *primary* input
to the fast-path listener design (O3). Note: the CLAUDE.md claim that `Endpoint._serializers`
does a hot-path `AddOrUpdate` is **stale** — already fixed by pre-seeding in `Endpoint.Compile`
(`Endpoint.cs:524-537`); update CLAUDE.md.

### T7 — Kafka-side config mismatches (MEDIUM; cheap to test)
- **Offset commit**: default `CommitMode.StoreThenAutoFlush` (`KafkaTopic.cs:64`) is fine;
  verify the client isn't on `PerMessage` (sync broker RTT per message) or an old version.
- **100Kb flow**: check `fetch.max.bytes` / `max.partition.fetch.bytes` / `queued.max.messages.kbytes`
  passthrough on their ConsumerConfig, and producer `linger.ms`/`batch.size` — "Kafka
  configuration identical for both flows" only covers what Wolverine passes through, and
  the effective-config inheritance only copies BootstrapServers/GroupId/SASL into per-topic
  overrides (`KafkaTopic.cs:145-213`) — a per-topic `ConfigureConsumer` silently DROPS other
  transport-level settings.
- **`StampConsumerGroupIdOnEnvelope` default true** (`KafkaTopic.cs:124`): inbound
  `envelope.GroupId` = consumer group name unless disabled — silently breaks GroupId-based
  partitioning/middleware keyed on GroupId (footgun for their semaphore keying and any GP work).
- Buffered mode acks-on-receive (`BufferedReceiver.cs:233-256`) — offsets commit before
  processing; at-most-once on crash. Not a latency issue but must be stated in guidance.

### T8 — Committer lock + consume-loop architecture (LOW-MEDIUM; measure, don't assume)
One blocking `Consume()` per iteration, one awaited `ReceivedAsync` per record
(`KafkaListener.cs:72-129`); Track+Complete each take a global lock per message
(`KafkaOffsetCommitter.cs:131-188`). Fine at thousands/s; matters at tens of thousands/s.
dotTrace will tell us.

---

## 2. Harnesses to build

### H1 — `KafkaPerfRig` (primary macro harness)
New console solution folder `src/Testing/KafkaPerfRig/` (kept out of CI like `Benchmarks`), three
processes mirroring the client topology, plus a native twin:

- **`Rig.ServiceA` / `Rig.ServiceB`**: replay a generated corpus (soccer-match-shaped: N
  concurrent "games", per-game monotonic event streams; 1Kb and 100Kb payload types) at a
  configurable rate/burst profile. Publish at end of a trivial Wolverine handler (like the
  client), stamping `t0` in a header.
- **`Rig.ServiceC`**: Wolverine consumer, handler simulates Marten work with a configurable
  `Task.Delay`/CPU-spin mix (calibrated ~9ms p50 to match their native handler number), then
  cascades a follow-on message (self-publish leg).
- **`Rig.NativeC` / `Rig.NativeAB`**: raw Confluent.Kafka twin of the same shape (their
  experiment reproduced) so both sides are measured by the SAME instrumentation.
- **Instrumentation — stage clock, not framework metrics**: one shared library stamping
  monotonic-ish stage timestamps as headers/in-proc records:
  `t0 publish-call → t1 broker-produce-ack → t2 consume-return → t3 handler-entry →
  t4 handler-exit → t5 ack/offset-stored`. HdrHistogram-style capture
  (`HistogramEx`/`HdrHistogram` NuGet), CSV dump per run, plus Wolverine's own
  `wolverine-execution-time`/`wolverine-effective-time` scraped via `dotnet-counters` for
  side-by-side "what the client's dashboard would say". Enable
  `HandlerExecutionDiagnosticsEnabled` to capture `transport_lag_ms`/`receive_dwell_ms`.
  Single box ⇒ no clock skew for cross-process wall-clock stages.
- **Run controller**: a small script (`rig.sh`) that sets the scenario via env vars, runs warmup
  (≥60s — codegen, JIT, consumer-group stabilization), then a fixed 10-min measurement window,
  and archives config + CSV + counter dumps per run ID. 2-hour soak reserved for the finale.
- **Topology**: local docker-compose Kafka (single broker — fine for relative comparisons; note
  absolute transit will beat the client's real cluster). Topics explicitly provisioned with
  ≥ 12 partitions (`.Specification(spec => spec.NumPartitions = N)` — auto-provision defaults to
  **1 partition** which invalidates every parallelism test; `KafkaTopic.cs:43,435`).

Reusable scaffolding: `src/Testing/Benchmarks/Driver.cs` + `targets.json` corpus,
`src/Persistence/LoadTesting` publisher shape, `src/Samples/AspireWithKafka` for wiring.

### H2 — Microbenchmark suite (BenchmarkDotNet)
Add `KafkaHotPathBenchmarks` to `src/Testing/Benchmarks` (resurrect project; it's not in either
.slnx — add to full solution only if it builds clean). Benchmarks, each general-path vs
hypothetical fast-path:
1. `KafkaEnvelopeMapper.MapIncomingToEnvelope` with a realistic 12-15-header message vs a
   hand-rolled fixed-schema reader (no dict, lazy decode, no re-scan).
2. `new Envelope()` (incl. wasted sequential-Guid) + header dictionary vs pooled/minimal envelope.
3. `TryFindMessageType` warm hit ×2 + `RequiresEncryption` in isolation.
4. `TryFindSerializer("application/json")` vs cached field.
5. `_executors[type]` + `GetType()` vs constant executor field.
6. `Envelope.Message` setter re-stamp vs raw field assignment.
7. `Executor.ExecuteAsync` fixed overhead: per-message CTS pair vs shared-deadline scheme.
8. `writeOutgoingOtherHeaders` (`Values.ToArray()` per send) vs precomputed set.
End-to-end micro: in-proc `Consume→handler-entry` latency, general pipeline vs fast-path
prototype.

### H3 — dotTrace protocol (when the box frees up)
Box: Apple M5 Max, 18 cores, 128GB. Rider 2025.1 bundles dotTrace; for scripted capture install
CLI tools first:
```bash
dotnet tool install -g JetBrains.dotTrace.GlobalTools   # dottrace attach/save-to-snapshot
dotnet tool install -g dotnet-counters dotnet-trace dotnet-gcdump
```
Protocol per scenario: start rig → warmup → `dottrace attach <pid-of-ServiceC>` in **Timeline**
mode for 120s of steady state (Timeline gives thread-state + lock-contention + GC lanes, which is
what T2/T3/T8 need; sampling mode as a second pass for pure CPU attribution). Also capture one
`dotnet-gcdump` mid-run for allocation census (T6) and `dotnet-counters monitor` for
`ThreadPool` queue length + GC pause. Name snapshots `<scenario>-<runid>.dtp` and keep with the
CSV archive. Analysis targets: time split across
`Consume / mapper / inbox-insert / queue-dwell(thread-wait) / handler / flush / commit`.

---

## 3. Intermediate steps (before the box is free)

1. **Build H1 + H2 skeletons now** — they don't need the perf box; smoke-run at low rate against
   docker Kafka on any machine to validate instrumentation plumbing (stage timestamps survive the
   mapper, histograms non-empty, native twin honest).
2. **Fix CLAUDE.md** stale `_serializers` note (T6).
3. **Client questionnaire** (§7) — answers change the matrix weights.
4. **Pre-registered predictions**: keep §1 predictions as-is; the write-up will score them.
5. Decide branch hygiene: rig + benchmarks land on `main` (inert, not in CI); any scratch
   instrumentation inside Wolverine itself stays on branch `perf/kafka-deep-dive`.

## 4. Experiment matrix (macro rig)

Axes, one change at a time from a fixed baseline
(**baseline** = client-shaped: Buffered everywhere, batch 10/10ms, semaphore-middleware
sequencing, 1Kb flow at ~8/s + 100Kb at ~0.6/s, self-publish leg on):

| # | Experiment | Theory | Levers |
|---|---|---|---|
| E1 | Endpoint mode sweep C: Buffered / Durable(PG) / Inline | T3,T4 | `UseDurableInbox()`, `ProcessInline()` |
| E2 | Sender batching sweep A/B: (100,250ms) default / (10,10ms) / (1,1ms) / Inline send | T1,T5 | `MessageBatchSize/Timeout`, endpoint mode |
| E3 | Sequencing: semaphore-middleware vs `PartitionProcessingByGroupId(Five/Seven/Nine)` vs none | T2 | `ListenerConfiguration.cs:110` |
| E4 | Parallelism: `MaxDegreeOfParallelism` 1/5/18/36 × `ListenerCount` 1/3/6 | T3 | `Endpoint.cs:212,424` |
| E5 | Back-pressure: `BufferingLimits` (1000,500) / (10000,5000) / effectively-off | T3 | `Endpoint.cs:259` |
| E6 | Commit mode: StoreThenAutoFlush / BatchCount(100) / BatchInterval(5s) / PerMessage | T7,T8 | `KafkaListenerConfiguration.cs:42-74` |
| E7 | Mapper: default 19-header mapper vs `JsonOnlyMapper`-style minimal interop mapper | T6 | `UseInterop` |
| E8 | Payload: 1Kb vs 100Kb × fetch/linger ConsumerConfig/ProducerConfig tuning | T7 | config passthrough |
| E9 | Self-publish leg: Kafka round-trip vs durable local queue vs buffered local queue | — | routing |
| E10 | Multi-handler: single combined chain vs `MultipleHandlerBehavior.Separated` (fanout via `FanoutMessageHandler` re-dispatch) vs 2 sticky local queues | client shape | `WolverineOptions.cs:27-41`, `[StickyHandler]` |
| E11 | Telemetry cost: default vs `TelemetryEnabled(false)` + success-log at Information vs Debug | T6 | `ListenerConfiguration.cs:325` |
| E12 | Durable batch-insert prototype (O1) vs current per-message insert | T4 | branch build |

Output per cell: p50/p95/p99 of each stage interval + throughput + GC/CPU counters; each cell
10-min window, 3 repetitions, report medians-of-percentiles.

## 5. GlobalPartitioning-equivalent experiments (E13 block)

The client wants same-key sequencing. Three Wolverine shapes to benchmark against their
co-partitioning + semaphore approach, cheapest first:

1. **Kafka co-partitioning + `PartitionProcessingByGroupId`** (Buffered): keep their topology,
   replace the semaphore middleware with the `ShardedExecutionBlock`. Needs
   `GroupByMessageKey()` or `DisableConsumerGroupIdStamping()` + explicit GroupId so the
   business key actually lands on `envelope.GroupId` (T7 footgun). No durability tax. This is
   the likely recommendation.
2. **`ProcessConcurrentlyByKey(slots)`** (`KafkaListenerConfiguration.cs:179-194`): same block
   but forces the durable inbox — measures the durability tax explicitly vs shape 1.
3. **Full `UseShardedKafkaTopics` GP** (`KafkaTransportExtensions.cs:300-338`): N topics + forced
   Durable on external AND companion local slots (`GlobalPartitionedMessageTopology.cs:49-58`)
   + bridge hop. Cluster-wide exclusivity guarantee, highest cost. Also note the May header bug
   they hit (`775a67373`, GP interceptor dropped correlation/tenant headers) shipped fixed in
   **5.39.0** — they can un-park; and `GlobalPartitionedInterceptor.ShouldIntercept` runs a LINQ
   check on EVERY envelope of every non-sharded listener once any GP topology exists
   (`GlobalPartitionedInterceptor.cs:135-151`) — include a "GP configured but message not
   GP-routed" cell to price that.

Deliverable: a decision table "which sequencing shape at which throughput/durability need" for
the docs.

## 6. Optimization candidates (post-measurement backlog, pre-registered)

Ordered by expected value; each gated on the matrix/microbench confirming its theory:

- **O1 (T4): batch the durable inbox for single-envelope transports.** Micro-batch
  `StoreIncomingAsync` behind the existing `_receivingOne` block (accumulate N/T like the
  committer does) and/or let `KafkaListener` consume-many → `ProcessReceivedMessagesAsync`
  (the multi-VALUES path already exists). Biggest structural win for durable Kafka.
- **O2 (T5): delete the per-message `Flush()` in `InlineKafkaSender`** — `ProduceAsync`'s ack is
  already awaited; flush belongs in `Dispose`/drain. Near-free fix, arguably a bug.
- **O3 (T6): single-type fast-path listener.** Seams already exist: `Endpoint.MessageType` +
  `DefaultIncomingMessage<T>()` (`Endpoint.cs:416`, `ListenerConfiguration.cs:456`),
  `Endpoint.DefaultSerializer`, per-endpoint `HandlerPipeline` construction
  (`ListeningAgent.cs:82-95`), `IHandlerPipeline` interface, `KafkaTopic.BuildListenerAsync`
  (`KafkaTopic.cs:224-269`). Design sketch: when an endpoint declares one message type + one
  serializer, build a `SingleTypeHandlerPipeline` holding a pre-resolved
  `(Type, IMessageSerializer, IExecutor, encryption-verdict)` tuple — skipping both string-keyed
  type lookups, the serializer lookup, the executor lookup, and the `Message`-setter re-stamp —
  paired with a minimal fixed-schema mapper (lazy header decode, no dictionary, no double decode,
  no wasted Guid) and receive-side envelope pooling (send side already pools, #2726/#2955;
  receive side does not — `KafkaTransportExtensions.cs:252`). Fall back to the general pipeline
  if the type header disagrees. Microbench first (H2 #1-6 quantify the ceiling), then prototype
  behind `ListenerConfiguration.OptimizeForSingleMessageType()`.
- **O4 (T6): mapper fixes independent of O3** — kill the incoming double-decode (decode once
  into locals, populate dict lazily), precompute the reserved-header set instead of
  `Values.ToArray()` per outgoing message, skip the `NewId` when the wire id will overwrite it.
- **O5 (T2/T6): cheap executor trims** — success log Information→Debug default (or sampled);
  CTS pooling/deadline scheme; record sub-1ms executions (fix the `> 0` drop — measurement bug).
- **O6 (T1): batching ergonomics** — document the debounce semantics loudly; consider a
  max-age flush (timer NOT reset per post, i.e. cap total wait at `MessageBatchTimeout`) as an
  opt-in `MessageBatchMaxAge`; revisit `MessageBatchMaxDegreeOfParallelism=1` default for Kafka
  (librdkafka is happy with concurrent produce).
- **O7 (T3): back-pressure without leaving the group** — Kafka `Pause()`/`Resume()` on assigned
  partitions instead of listener dispose/rebuild (avoids rebalance storms). Bigger change;
  price it only if E5 shows oscillation.
- **O8 (T8): committer lock → per-partition striping** if dotTrace shows contention.

## 7. Questions back to the client (send early)

1. Wolverine version? (Determines GP header fix ≥5.39.0, commit-mode defaults ≥ the #3134 fix,
   raw-JSON mapper fixes ≥6.20.0.)
2. Endpoint modes per service, exactly — and where did "batch size = 10, batch timeout = 10ms"
   come from / is it set per subscriber endpoint? (Not a Wolverine default.)
3. Where do their "transit/effective" timestamps start/stop in BOTH harnesses — and are they
   comparing their own stamps or Wolverine's `wolverine-effective-time` (which includes flush +
   ack and is clock-skew sensitive)?
4. Is the semaphore middleware inside the handler chain (⇒ counted as execution time)?
5. Avro serializer: registered as endpoint `DefaultSerializer`, or via a custom
   `IKafkaEnvelopeMapper`? Do they rely on `envelope.GroupId`, and do they know inbound GroupId
   defaults to the consumer-group name (`StampConsumerGroupIdOnEnvelope`)?
6. Producer/consumer configs: `linger.ms`, `batch.size`, fetch sizes — and any per-topic
   `ConfigureConsumer` overrides (which silently drop transport-level settings other than
   bootstrap/group/SASL).

## 8. Sequencing & exit criteria

- **Wave 0 (now, no perf box)**: H1+H2 skeletons, smoke runs, client questionnaire, CLAUDE.md
  fix. Exit: rig produces believable stage histograms for both Wolverine and native twins at
  low rate.
- **Wave 1 (box free)**: baseline + E1-E8 singles; dotTrace on baseline, E1-durable, E3.
  Exit: ≥80% of the client's p50 transit and execution gaps attributed to named mechanisms
  with numbers.
- **Wave 2**: E9-E11 (self-publish + multi-handler + telemetry), E13 GP-equivalents.
  Exit: sequencing-shape decision table drafted.
- **Wave 3**: O2 + O4 + O5 quick wins implemented and re-measured (E-cells rerun); O1 and O3
  prototyped on `perf/kafka-deep-dive` with E12 verdicts.
  Exit: PRs for confirmed wins; microbench deltas recorded in PR descriptions.
- **Wave 4**: update the "Performance Tuning" section of
  `docs/guide/messaging/transports/kafka.md` (seeded 2026-07-18 with qualitative guidance) with
  measured numbers from the ledger + reply to the client mapping each of their numbers to a
  mechanism and a config change; 2-hour soak on the final recommended configuration.

## 9. Measured-wins ledger (for release notes / blog posts)

Every confirmed optimization gets a row here as it lands — filled in during Waves 3-4, kept
current so release notes and blog drafts can lift numbers directly instead of re-deriving them.
Rules: numbers only from rig/microbench runs archived with a run ID; record the exact scenario
(E-cell) and config so the claim is reproducible; before/after from the SAME rig version; phrase
the one-liner the way a release note would say it (user-visible effect, not internals).

| Optimization | PR | Scenario (E-cell) | Metric | Before | After | Release-note one-liner |
|---|---|---|---|---|---|---|
| T1/O6 root cause: `BatchingChannel` debounce → max-age (JasperFx) | jasperfx PR (fix/batching-channel-max-age) | E2 batch-default (100/250ms) vs batch-1-1, local rig 2026-07-19 | transit p50 1Kb@8/s | **5,767ms** (default 100/250ms; debounce never fires under steady trickle); 263ms lone-msg 100Kb | bounded ≤ batch timeout by construction (re-measure after jfx pin bump) | "Kafka/transport sender batching now flushes within the batch timeout — a steady message trickle no longer postpones sends for seconds" |
| Client-shaped gap attribution (T1) | — | baseline (10/10ms) vs native-anchor | transit p50 1Kb@8/s | 19.9-21.2ms vs native 8.7-9.3ms (+11ms = client's 25.8 vs 7.9 reproduced) | batch(1,1ms): 7.6ms ≈ native; send-inline: 8.4-8.9ms ≈ native | "batch size/timeout tuning collapses the transit gap to native" |
| T4 durable inbox ceiling | — (O1 pending) | thru-durable 2000/s vs thru-buffered | transit p50 | durable **14,127ms** (backlog; per-msg INSERT gates consume loop) vs buffered 32ms vs native 8.5ms | O1 batched insert TBD | — |
| O2 inline-sender per-message Flush() removed | quick-wins PR | send-inline @8/s | transit p50 | 8.4ms (main) | 8.9ms (branch, noise—penalty is a concurrency tax, not visible single-threaded @8/s) | "Inline Kafka senders no longer block on a full producer flush after every send" |
| O4 mapper fixes (outgoing reserved-set cache, Kafka dict-first incoming) | quick-wins PR | H2 microbench 2026-07-19 | MapIncoming / MapOutgoing mean+alloc | 1,240ns/3,848B; 619ns/2,392B | **983ns/2,768B (-21%/-28%); 514ns/2,056B (-17%/-14%)** | "every Kafka receive maps ~21% faster with ~28% less allocation; every send ~17% faster" |
| O5 sub-1ms executions recorded | quick-wins PR | metrics | wolverine-execution-time | sub-1ms samples silently dropped (long, truncate, >0) | double histogram, all samples | "execution-time metrics no longer drop sub-millisecond handlers" |

### Pre-fix throughput baselines (2026-07-19, fresh broker, worktree @f6f125f10: jfx 2.30.0, pre-O1)

Max-throughput cells (uncapped publisher, no handler work, 12 partitions; consumed_per_sec over
trimmed receive window). Pre-registered expectations for the after-run in parentheses:

| cell | BEFORE (jfx 2.30.0, pre-O1) | AFTER (jfx 2.30.1 + O1, measured 2026-07-19) | prediction verdict |
|---|---|---|---|
| max-native | **107,669/s** | 106,296/s | anchor stable ✓ |
| max-buffered | **10,726/s** | 10,872/s | unchanged as predicted ✓ (gap to native = pipeline/publisher cost → O3) |
| max-durable | **1,460/s** | **2,671/s (+83%)** | O1 real but < the 5-10x hoped; remaining ceiling = per-message mark-handled UPDATE on completion (**O1b follow-up: batch the handled updates**) |
| thru-durable @2000/s transit p50 | **14,127ms, unbounded backlog** | **31.5ms, keeps up** | **O1 user-visible headline** ✓ |
| thru-buffered @2000/s transit p50 | 32.4ms | 33.7ms | unchanged as predicted ✓ (size-flush dominates) |
| batch-default (100,250ms @8/s) transit p50 | **5,767ms** | **136ms (p99 262ms)** | timeout-bounded as predicted ✓ — the jfx max-age headline |
| batch-default lone msgs (100Kb @0.6/s) | 263ms | 264ms | unchanged as predicted ✓ (always paid full timeout) |
| baseline (10,10ms @8/s) transit p50 | 19.9ms | 20.0ms | unchanged as predicted ✓ |

### Release-notes draft (6.21, Kafka/messaging performance — numbers from the ledger above)

> **Message batching now flushes within the configured timeout.** The shared sender-batching
> channel treated its timeout as a quiet-period debounce: every published message reset the
> timer, so a steady stream could postpone sends until a full batch (default 100) accumulated.
> Measured on a 1Kb stream at 8 msg/s with default settings, publish-to-consume p50 latency was
> **5.8 seconds before the fix and 136ms after** (bounded by the 250ms default batch timeout;
> tighten `MessageBatchSize`/`MessageBatchTimeout` for latency-sensitive routes). Requires
> JasperFx 2.30.1. Applies to every transport that sends through Wolverine's batched sender.
>
> **Durable Kafka listeners persist incoming batches.** A durable (inbox-backed) Kafka listener
> used to make one database insert per record inline on the consume loop, capping consumption
> around **1,500 msg/s** locally and falling unboundedly behind at 2,000 msg/s (14s+ delivery
> latency in a 2-minute window). The listener now drains up to `MaximumMessagesToReceive`
> (default 100) already-fetched records into a single batched inbox insert: the same 2,000
> msg/s load now runs at a steady **32ms** delivery p50, and maximum sustained durable
> throughput measured **+83%** (1,460 → 2,671 msg/s on the local rig).
>
> Also in this wave (#3501): every Kafka receive maps ~21% faster with ~28% less allocation and
> every send ~17% faster; inline Kafka senders no longer block on a full producer flush per
> message; `wolverine-execution-time` no longer silently drops sub-millisecond executions; the
> per-message success log now defaults to Debug (`Policies.MessageSuccessLogLevel` restores
> Information).

Negative results so far (2026-07-19 local rig):
- **T2 semaphore sequencing**: no measurable execution-time inflation at 8/s across 20 games
  (semaphore essentially uncontended at client-shaped rates). The client's 20.1 vs 8.9ms
  execution gap needs their real contention profile — likely hot streams; not reproducible
  at rig rates. Keep the PartitionProcessingByGroupId recommendation but don't promise numbers.
- **T5 inline Flush()**: invisible at 8/s single-threaded sends (~0.5ms noise). It's a
  concurrency/throughput tax; the fix stands on inspection + code semantics, not a rig delta.
- **T3 back-pressure oscillation**: never triggered — buffered dwell stayed <1ms at 2000/s
  (fast handler drains ahead of the 1000-message limit). Needs a slow-handler high-rate cell.
- **Resolution-chain lookups (part of T6)**: microbench shows warm TryFindMessageType 11.7ns,
  TryFindSerializer 5.4ns, Message-setter re-stamp 4.7ns — trivial. The fast-path listener's
  real ceiling is the mapper + allocations, not the ImHashMap reads.

## 10. Risks / honesty notes

- Single-box, single-broker rig understates broker transit and network effects; all conclusions
  are relative (Wolverine vs native on identical infra), not absolute.
- The client's numbers include Marten writes we only simulate; durable-mode conclusions about
  *their* DB contention need their schema (inbox and Marten sharing a PG instance compounds T4).
- M5 Max (ARM) GC/JIT behavior differs from their production x64 Linux — CPU-bound
  microbenchmark ratios transfer, absolute numbers don't.
- Buffered-mode results must always carry the at-most-once caveat (offset stored on receive).
