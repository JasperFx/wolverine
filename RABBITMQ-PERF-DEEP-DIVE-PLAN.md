# RabbitMQ Performance Deep-Dive Plan (2026-07-18)

Goal: measure Wolverine-over-RabbitMQ overhead vs raw RabbitMQ.Client 7.x across endpoint modes,
produce throughput-tuning guidance, and land the confirmed optimizations. Companion/umbrella:
`KAFKA-PERF-DEEP-DIVE-PLAN.md` (wolverine#3490) — the metrics semantics (§0 there:
execution-time vs effective-time definitions, sub-1ms sample drop, middleware-blocking
inclusion), the resolution-chain/mapper base costs, the BatchedSender debounce, and the
back-pressure agent mechanics are shared and not restated in full here.

All file:line cites verified against `main` @ 6.20.0. Client lib: RabbitMQ.Client **7.1.2**
(fully async).

---

## 0. Transport-specific facts that frame everything

- **Default endpoint mode is `Inline`** (`RabbitMqEndpoint.cs:21`, re-asserted
  `RabbitMqQueue.cs:43`) — unlike the core default of BufferedInMemory. Combined with
  `ConsumerDispatchConcurrency` default **1** (`WolverineRabbitMqChannelOptions.cs:28`), an
  out-of-the-box RabbitMQ listener consumes **one message at a time, end-to-end through the
  handler**, per endpoint. `MaxDegreeOfParallelism` is irrelevant for inline; the only scaling
  knobs are `ListenerCount` (N channels+consumers, `ListeningAgent.cs:365-375`) and the
  transport-wide dispatch concurrency.
- **RabbitMQ never hands the receiver `Envelope[]`** — the consumer's only call is the
  single-envelope `ReceivedAsync` (`WorkerQueueMessageConsumer.cs:100`). The batched
  multi-VALUES durable-inbox insert (`DurableReceiver.cs:608-668`) is therefore **unreachable**;
  durable RabbitMQ pays one `StoreIncomingAsync` INSERT per message (`DurableReceiver.cs:494`)
  even though prefetch delivers natural batches. Same structural gap as Kafka (#3490 T4); SQS
  and ASB do *not* have it.
- **Prefetch defaults** (`RabbitMqQueue.cs:64-83`): Buffered/Durable → `MaxDegreeOfParallelism
  × 2`; Inline → **100** (mostly useless for inline beyond hiding per-message network latency).
- **Publisher confirms default OFF** (`WolverineRabbitMqChannelOptions.cs:14,21`) — fast but
  fire-and-forget; when enabled, RabbitMQ.Client 7 awaits the broker ack inside
  `BasicPublishAsync` = one RTT per publish with **no confirm windowing** in Wolverine.
  `mandatory` is hardcoded false with no `BasicReturn` handler (`RabbitMqSender.cs:91`) —
  unroutable messages silently drop.
- **No BatchedSender**: `RabbitMqSender` is a plain `ISender`; every mode funnels to
  per-envelope `BasicPublishAsync` on one shared channel per endpoint (`RabbitMqSender.cs:67-92`).
  So the Kafka debounce theory does NOT apply here; per-publish costs do.
- **Acks**: manual, per message, via RetryBlock → `BasicAckAsync(deliveryTag, multiple: true)`
  (`RabbitMqListener.cs:317-320`). `multiple:true` makes most acks cumulative-redundant — a
  coalescing opportunity, and worth an eyebrow for out-of-order completion semantics.
- **Back-pressure stop disposes the channel** (`ListeningAgent.cs:451-484` →
  `RabbitMqChannelAgent.cs:218-232`): all unacked/prefetched messages redeliver. Sawtooth load
  at the 1000/500 `BufferingLimits` thresholds = redelivery churn (RabbitMQ analogue of Kafka's
  rebalance oscillation).

## 1. Theories of overhead, ranked

- **R1 (HIGH): inline-by-default single-file consumption.** Most "Wolverine RabbitMQ is slow"
  reports should reproduce as: default endpoint → 1 msg at a time × handler latency.
  Prediction: throughput scales ~linearly with `ListenerCount` and with switching to
  Buffered; docs need a loud "scaling RabbitMQ consumption" section.
- **R2 (HIGH, durable leg): per-message inbox INSERT** despite prefetched natural batches.
  Prediction: durable throughput ceiling ≈ 1/insert-RTT per listener; micro-batching the
  inserts (RO1) is the biggest structural win.
- **R3 (MEDIUM): per-message mapper + publish allocations** — incoming double-decode of every
  reserved header (`RabbitMqEnvelopeMapper.cs:74-100`), `body.ToArray()` copy per delivery
  (`WorkerQueueMessageConsumer.cs:49`), outgoing `Values.ToArray()` + O(n×m) `Contains`
  (`EnvelopeMapper.cs:391-397`), `new BasicProperties` + header `Dictionary` per publish
  (`RabbitMqSender.cs:82-86`), Guid parse/ToString per message. CPU/GC pressure at high rates;
  quantify via microbench (shared suite from #3490 H2, RabbitMQ variants).
- **R4 (MEDIUM): publisher-confirm mode cliff.** Confirms off = fastest but silently lossy on
  broker failure; confirms on = per-publish RTT serialization on the caller. Measure both;
  guidance must state the cliff and the durable-outbox interaction (outbox + confirms = paying
  twice for the same guarantee?).
- **R5 (MEDIUM): back-pressure channel teardown** redelivery churn under sustained load —
  p99 spikes + duplicate executions (buffered mode already acked, so its buffered backlog is
  lost instead; #3137 family).
- **R6 (LOW-MEDIUM): ack RPC per message** through a RetryBlock (`RabbitMqChannelCallback.cs:16-34`)
  — Task + try/catch per ack; `multiple:true` means a watermark committer (like Kafka's
  `KafkaOffsetCommitter`) could ack once per N.
- **R7 (LOW): topic-routed sends may deserialize the body per send** to compute the routing key
  (`RabbitMqSender.cs:42-61`) — only for topic-exchange publishing; check with a targeted cell.
- **R8 (LOW): `DeferAsync` = ack + full republish** (`RabbitMqEnvelope.cs:34-47`) — every
  retry-requeue pays a publish; only matters under high failure rates.

## 2. Harness

Extend the `KafkaPerfRig` from #3490 into a transport-pluggable rig (`TransportPerfRig`): same
three-service topology, same stage-clock instrumentation (t0 publish-call → t1 broker-ack →
t2 consume-callback → t3 handler-entry → t4 handler-exit → t5 ack), same HdrHistogram CSV +
`dotnet-counters` capture, same dotTrace Timeline protocol. RabbitMQ adapter + a **native twin**
on raw RabbitMQ.Client 7 (`AsyncEventingBasicConsumer`, manual acks, same prefetch). Broker:
docker-compose RabbitMQ (5672). Classic AND quorum queues in the matrix — quorum's fsync path
changes everything and RabbitMQ 4.x defaults quorum `delivery-limit=20` (poison interplay noted
in `TRANSPORT-CAPABILITY-RESEARCH-2026-07-18.md`).

## 3. Experiment matrix

Baseline: default endpoint (Inline, prefetch 100, confirms off, classic queue), 1Kb payloads,
steady 500/s, ~10ms simulated handler.

| # | Experiment | Theory | Levers |
|---|---|---|---|
| RE1 | Mode sweep: Inline / Buffered / Durable(PG) | R1,R2 | `ProcessInline()`, `BufferedInMemory()`, `UseDurableInbox()` |
| RE2 | Inline scaling: ListenerCount 1/5/10 × ConsumerDispatchConcurrency 1/5/20 | R1 | `ListenerCount`, `ConfigureChannelCreation` |
| RE3 | Prefetch sweep per mode: 10/100/500/2×MDOP | R1,R6 | `PreFetchCount(ushort)` |
| RE4 | Publisher confirms: off / on / on+durable-outbox | R4 | `WolverineRabbitMqChannelOptions` |
| RE5 | Queue type: classic vs quorum vs stream-as-queue | — | `UseQuorumQueues()` etc. |
| RE6 | Back-pressure: default (1000/500) vs raised limits under 2× overload burst | R5 | `BufferingLimits` |
| RE7 | Mapper: default vs minimal interop mapper; 100Kb payloads | R3 | `UseInterop` |
| RE8 | Topic-exchange routing vs direct queue sends | R7 | routing config |
| RE9 | Durable batch-insert prototype (RO1) vs per-message | R2 | branch build |
| RE10 | Sequencing shapes (§4) | — | see below |
| RE11 | Failure-path cost: 5% handler failures → Defer/republish churn | R8 | error policy |

Output per cell: stage p50/p95/p99 + throughput + GC/CPU; 3 reps; medians-of-percentiles.
Same measured-run archival rules as #3490.

## 4. Sequencing / GlobalPartitioning-equivalents

RabbitMQ has **no** broker-native sequencing surface in Wolverine today: no
single-active-consumer (`x-single-active-consumer` unsupported — settable only via raw
`Queue.Arguments`), no consistent-hash exchange, streams have no offset surface. Shapes to
benchmark:
1. **One queue + `PartitionProcessingByGroupId(slots)`** (Buffered) — consumer-side sequencing,
   no topology change. Likely the cheapest recommendation.
2. **`UseShardedRabbitQueues(base, N)`** (`RabbitMqTransportExtensions.cs:503-515`) — N plain
   queues, Wolverine-side hash routing; inside a `GlobalPartitioned(...)` topology the slots are
   forced Durable (adds the R2 tax — quantify).
3. **Manual `x-single-active-consumer` via Arguments + ListenerCount>1** — exclusive-consumer
   failover semantics without Wolverine support; measure to motivate (or kill) the capability-doc
   item for a first-class SAC helper.

## 5. Optimization backlog (gated on matrix confirmation)

- **RO1 (R2): micro-batch the durable inbox for push transports.** Either a small
  accumulate-window in `DurableReceiver.ReceivedAsync` (shared with Kafka O1 — design once,
  benefit both) or consumer-side aggregation into `Envelope[]`.
- **RO2 (R6): ack watermark coalescing** — deliberate use of `multiple:true`: ack once per
  N/interval from a committer-style tracker instead of per message.
- **RO3 (R3): mapper/publish allocation fixes** — kill incoming double-decode, precompute the
  reserved-header set (shared with Kafka O4), investigate pooling the header dictionary,
  skip `body.ToArray()` where the pipeline consumes synchronously (risky — client recycles
  memory; measure first).
- **RO4 (R4): publisher-confirm windowing** — allow K outstanding confirms instead of
  await-per-publish, if confirms-on proves popular.
- **RO5 (R5): back-pressure without channel teardown** — `BasicCancelAsync` (consumer cancel,
  channel stays open; already what `StopAsync` does at `RabbitMqListener.cs:99-115`) without the
  `DisposeAsync` channel close; resume = re-consume on the same channel.
- **RO6 (R1): ergonomics/docs** — surface `ConsumerDispatchConcurrency` per endpoint (currently
  transport-wide), and a prominent "scaling consumption" docs section covering the
  Inline-default + dispatch-1 reality.
- **RO7: single-active-consumer helper** (capability-doc crossover) if RE10.3 motivates it.

## 6. Measured-wins ledger (for release notes / blog posts)

### Measured 2026-07-19 (local rig; BEFORE = worktree @f6f125f10/jfx 2.30.0, AFTER = RO1 + mapper flag + jfx 2.30.1)

| Cell | BEFORE | AFTER | Verdict |
|---|---|---|---|
| r-default (out-of-box, 1Kb @8/s) transit p50 | 2.385ms | 2.396ms | native parity (r-native 2.1ms); mapper flag no regression |
| r-batch-buffered (BufferedInMemory send, 100/250ms) transit p50 | 2.015ms | 1.684ms | **Rabbit sends never routed through the debounced batching channel — unaffected by the GH-3490 bug both sides** |
| r-thru-inline-1 @2000/s, 5ms handler | 178.7/s | 177.9/s | R1 confirmed: inline single-file; native single-consumer twin = 166/s → NOT Wolverine overhead. Scaling: ListenerCount(4)=720/s, buffered=1,999/s @2ms p50 |
| r-thru-durable @2000/s | 1,688/s, 14.7s backlog p50 | **1,999.9/s, 0.8ms p50** | **RO1 headline** |
| r-max-durable | 1,086/s | **3,101/s (+186%)** | RO1; remaining ceiling = per-message mark-handled (O1b, shared w/ Kafka) |
| r-max-inline | 29,638/s | 27,771/s | ~unchanged (run variance) |
| r-max-buffered | 16,441/s | 15,330/s | ~unchanged; buffered max < inline max both sides — back-pressure churn suspected (R5/RO5, NOT addressed this release) |

Negative results: R3 mapper flag shows no ms-scale rig delta (µs-scale, matches Kafka µbench
transfer); the GH-3490 debounce never applied to Rabbit's sender path (default inline AND
buffered both unaffected — checked both directions); R5 buffered-below-inline max anomaly
recorded but unfixed (RO5 follow-up).

### Measured 2026-07-27 (wave 2; local rig, same box, `perf/3492-rabbitmq-wave2` vs `origin/main` @073312262)

| Cell | BEFORE | AFTER | Verdict |
|---|---|---|---|
| r-thru-inline @2000/s, 5ms handler, default | 163.7/s | — | R1 reconfirmed on this box |
| ...same cell + `ConsumerDispatchConcurrency(5)` | 163.7/s | **828/s (5.1x)** | **RO6 headline** |
| ...same cell + `ConsumerDispatchConcurrency(20)` | 163.7/s | **1,999/s (12.2x)** | RO6 — keeps up with the full offered load |
| r-max-inline (uncapped, 0ms handler) | 41.0k/41.0-44.2k/s | 42.7k/41.3k/s | ack `multiple:false` is throughput-neutral (change itself deferred, see below) |
| r-max-native (anchor) | 51.0k/s | — | Wolverine inline ≈ 84% of the native twin at max throughput |

**RO2 ack-watermark coalescing: REJECTED on measurement.** A prototype that batched settlements
behind a 5ms/prefetch-sized `BatchingChannel` and issued one cumulative `basic.ack` per batch
measured **37.7-39.5k/s vs a 41.0-44.2k/s baseline — a ~10% regression** across three runs each.
The premise was wrong: `basic.ack` is a fire-and-forget AMQP frame, not an RPC, so coalescing
saves no round trips while the extra channel hops cost real throughput. The native twin acks per
message too, which is why acks were never the inline gap.

**RO2 ack semantics (`multiple: true` → `false`): DEFERRED, and here is why it is not a one-liner.**
`BasicAckAsync(tag, multiple: true)` on every completion also acks every lower delivery tag on the
channel, so out-of-order completion under a concurrent listener acks messages still in the handler
pipeline — a real at-least-once hole. But that sweep turns out to be **load-bearing**: it is
currently the only thing settling deliveries that some paths never acknowledge at all. Flipping it
to `multiple: false` in isolation makes those deliveries leak.

Measured, after a full `Wolverine.RabbitMQ.Tests` run against a freshly deleted queue:

| Build | quorum1 after the full suite |
|---|---|
| `origin/main` | 0 messages |
| `multiple: false` | **1 message ready (leaked)** |

CI caught it twice on fresh containers: `quorum_queue_compliance.will_requeue_and_increment_attempts`
fails 2-of-2 attempts on the ack branch and passes on two sibling PRs off the same base that carry
no RabbitMQ changes. The isolated retry runs fail because the leaked message is still sitting in the
queue from the full run — locally the same test passes against a clean queue and fails against a
dirty one, on both branches.

The fix is to find and settle the leaking path (prime suspect:
`RabbitMqInteropFriendlyCallback.MoveToErrorsAsync` posts a copy to the dead-letter queue and never
acks or nacks the original, unlike `RabbitMqChannelCallback.moveToErrorQueueAsync` which nacks), NOT
to keep the sweep. Split onto its own branch so the RO6 win is not held hostage to it.

Same rules as #3490 §9: rows only from archived rig runs, exact E-cell + config recorded,
before/after from the same rig version, one-liner phrased for release notes. Log negative
results below the table.

| Optimization | PR | Scenario (RE-cell) | Metric | Before | After | Release-note one-liner |
|---|---|---|---|---|---|---|
| RO1 batched durable inbox | #3512 | r-max-durable | throughput | 1,086/s | 3,101/s | Durable RabbitMQ listeners persist prefetched deliveries in one batched insert: +186% max throughput |
| RO6 per-endpoint `ConsumerDispatchConcurrency` | this wave | r-thru-inline @2000/s, 5ms handler | throughput | 164/s | 1,999/s | Inline RabbitMQ listeners can now scale consumption per endpoint instead of transport-wide: 12x on a 5ms handler |
| RO2 ack coalescing | — | r-max-inline | throughput | 41-44k/s | 37.7-39.5k/s | REJECTED — `basic.ack` is not an RPC; batching costs ~10% and saves nothing |
| RO2 ack semantics (`multiple: false`) | deferred | full RabbitMQ suite | messages leaked into quorum1 | 0 | 1 | DEFERRED — the cumulative sweep is load-bearing; fix the unsettled path first |

## 7. Sequencing & exit criteria

- **Wave 0 (now)**: transport adapter + native twin in the rig; smoke-run.
- **Wave 1 (box free)**: RE1-RE8; dotTrace on RE1-durable and RE2.
  Exit: mode-by-mode overhead vs native quantified; R1/R2 confirmed or killed.
- **Wave 2**: RE10 sequencing shapes + RE9/RE11 prototypes.
- **Wave 3**: land confirmed RO items; re-run touched cells; fill the ledger.
- **Wave 4**: update `docs/guide/messaging/transports/rabbitmq/performance.md` (seeded
  2026-07-18 with qualitative guidance) with measured numbers + release-note bullets from the
  ledger.

## 8. Risks / honesty notes

- Single-node docker broker: no cluster/mirroring effects; quorum-queue numbers on one node
  understate replication cost — relative comparisons only.
- Confirms-off cells measure a fire-and-forget publish; never quote them against a native twin
  running confirms-on.
- RabbitMQ full test suite is load-sensitive on CI (~10 min, chronic timeout history) — the rig
  must not join CI.
