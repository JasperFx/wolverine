# Release-notes draft — messaging performance wave (GH-3490 / GH-3492 / GH-3493)

> Draft for the next Wolverine release's notes (slots alongside the conjoined-tenancy epic and
> the 7/18 transport quick wins already queued for the same release). All numbers are from the
> GH-3490/3492 load rigs (single box, single broker, M5 Max) — relative comparisons against a
> raw-client "native twin" measured by the same instrumentation, not absolute promises.

---

## Headline: sender batching no longer sits on your messages

Wolverine's shared sender-batching channel (used by every transport when a subscriber endpoint
is Buffered or Durable) treated its `MessageBatchTimeout` as a *quiet-period debounce*: every
published message reset the flush timer, so any steady stream faster than the timeout postponed
the send until a full `MessageBatchSize` accumulated.

With the default `(100, 250ms)` settings, a modest 8 msg/s stream measured **5.8 seconds** of
publish-to-consume p50 latency. After the fix (JasperFx 2.30.1, picked up in this release) the
timeout is the **maximum age of a batch**: the same stream measures **136ms p50 / 262ms p99**,
bounded by the configured timeout. Rate-controlled and high-throughput paths are unchanged —
full batches always flushed immediately and still do.

Notes per transport:
- **Kafka**: applies to all Buffered/Durable subscriber routes (the default). This was the
  root cause of the "Wolverine-over-Kafka is 3-12x slower than native" report in GH-3490 —
  with tuned batching `(1, 1ms)` or `SendInline()`, Wolverine now measures at parity with a
  raw Confluent.Kafka producer (7.6-8.9ms vs 8.7ms transit p50 on the rig).
- **RabbitMQ**: unaffected. Subscriber endpoints default to *inline* sending, which never
  batched, and buffered RabbitMQ sends don't route through the debounced batching channel
  either — measured at 2.0ms transit p50 before the fix and 1.7ms after with `(100, 250ms)`
  batching configured. Default RabbitMQ routes already measure at native parity (2.4ms vs
  2.1ms p50 on the rig).
- Latency-sensitive, low-rate routes should still tune `MessageBatchSize`/`MessageBatchTimeout`
  down — the timeout is now an honest worst-case latency floor, but it is still a floor.

## Durable (inbox-backed) listeners: batched persistence

Durable endpoints used to make **one database insert per message, inline on the transport's
consume loop** — the inbox write RTT was the consumption ceiling.

- **Kafka** (`GH-3490`): durable listeners now drain up to `MaximumMessagesToReceive`
  (default 100) already-fetched records per consume pass and persist them with a single
  multi-VALUES insert. Measured: a 2,000 msg/s stream went from **unbounded backlog (14s+ and
  climbing)** to a steady **32ms** delivery p50; maximum sustained durable throughput went from
  **1,460 to 2,671 msg/s (+83%)**.
- **RabbitMQ** (`GH-3492`): durable listeners now coalesce prefetched deliveries for up to 5ms
  (max-age, not debounce) into the same batched insert path, with per-message acks still issued
  only after persistence. Before: **1,086 msg/s** ceiling, and a 2,000 msg/s stream fell
  unboundedly behind (14.7s delivery p50 in a 2-minute window). After: the same 2,000 msg/s
  stream runs at **0.8ms** delivery p50, and maximum sustained durable throughput measured
  **3,101 msg/s (+186%)**.
- The batched-arrival path now applies the exact same per-envelope semantics as one-at-a-time
  arrival (serializer unwrap for MassTransit-style interop, dead-lettering of unidentifiable
  messages, expiry, drain-time latching) — previously these guards only ran on the
  single-message path.
- The remaining durable ceiling is the per-message "mark handled" update after execution;
  batching that is a known follow-up.

## Scaling RabbitMQ consumption (docs + measured guidance)

Out-of-the-box RabbitMQ listeners are *Inline*: one message at a time, fully processed and
acked before the next — measured at **179 msg/s** against a 2,000 msg/s load with a 5ms
handler. That is not Wolverine overhead: an equivalent single-consumer raw RabbitMQ.Client
loop measures **166 msg/s** on the same rig. It's the single-file consumption pattern.
`BufferedInMemory()` holds the same load at **2ms** p50, and `ListenerCount(4)` scales inline
consumption ~4x. The new "Performance Tuning" page in the RabbitMQ docs covers the trade-offs
(including buffered mode's ack-on-receive loss window).

## Kafka receive/send hot path

- The envelope mapper no longer decodes every incoming header twice: **~21% faster mapping,
  ~28% less allocation per received message** (1,240ns/3,848B → 983ns/2,768B); outgoing
  mapping is ~17% faster / 14% lighter. RabbitMQ's mapper gets the same incoming optimization.
- `SendInline()` Kafka senders no longer issue a blocking full-producer `Flush()` after every
  send (it also blocked on every other in-flight message sharing the producer — inline sends,
  broker-per-tenant sends, and liveness pings all paid it).

## SQS: batched sends no longer silently drop rejected entries (GH-3493)

`SendMessageBatch` responses report per-entry failures (throttling, oversize); Wolverine never
inspected them, so rejected entries were treated as sent and **silently lost**. Failed entries
are now routed back through the sender's retry machinery individually, and an exception on a
later chunk of a large batch no longer re-sends chunks SQS had already accepted.

## Metrics and logging changes to be aware of

- **`wolverine-execution-time` is now a `double` histogram and records sub-millisecond
  executions.** Previously durations were truncated to whole milliseconds and sub-1ms samples
  were silently dropped — biasing the histogram upward for fast handlers (and skewing exactly
  the kind of framework-vs-native comparison GH-3490 reported). Dashboards keyed to the metric
  name/unit are unaffected; the point type changes from integer to floating point.
- **The per-message "successfully processed" log now defaults to `Debug`** (was `Information`)
  — a per-message Information log is a measurable tax on hot listeners. Restore the old
  behavior with `opts.Policies.MessageSuccessLogLevel(LogLevel.Information)`.
- `wolverine-execution-time` still measures the handler *plus all middleware* — including time
  blocked in custom middleware (semaphores, locks). If you sequence by key, prefer
  `PartitionProcessingByGroupId()` / `ProcessConcurrentlyByKey()` over hand-rolled gating; the
  wait then happens outside worker slots and outside the execution metric.

## New knobs

| API | Transport | Purpose |
|---|---|---|
| `ListenToKafkaTopic(...).MaximumMessagesToReceive(n)` | Kafka | Cap (or disable with 1) the durable consume-loop drain batch. Default 100. |
| `ListenToRabbitQueue(...).MaximumMessagesToReceive(n)` | RabbitMQ | Cap (or disable with 1) durable delivery coalescing. Default 100. |

## For contributors

The measurement harnesses ship in-repo (outside the solutions/CI): `src/Testing/KafkaPerfRig/`
(Kafka + RabbitMQ Wolverine/native twin rigs, stage-clock instrumentation, `cells*.sh`
experiment sweeps) and `src/Testing/Benchmarks/` (`KafkaHotPathBenchmarks`). The experiment
ledgers with every measured cell — including the negative results — live in the GH-3490 and
GH-3492 plan documents.
