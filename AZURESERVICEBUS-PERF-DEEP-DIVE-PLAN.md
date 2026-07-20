# Azure Service Bus Performance Deep-Dive Plan (2026-07-18)

Goal: measure Wolverine-over-ASB overhead vs the raw Azure.Messaging.ServiceBus SDK across
endpoint modes, validate the just-landed PrefetchCount work before release, fix the bug-shaped
session findings, and produce throughput-tuning guidance. Companion/umbrella:
`KAFKA-PERF-DEEP-DIVE-PLAN.md` (wolverine#3490) — shared metrics semantics, resolution-chain
costs, BatchedSender mechanics, back-pressure behavior live there.

All file:line cites verified against `main` @ `a53be88c5` (post-6.20.0 — PrefetchCount is
**unreleased**).

---

## 0. Transport-specific facts that frame everything

- **Three listener shapes** (`AzureServiceBusTransport.Listening.cs:62-141`): sessions →
  hand-rolled `AzureServiceBusSessionListener`; Inline → `ServiceBusProcessor`; Buffered/Durable
  (default) → `ServiceBusReceiver` batch loop pulling `MaximumMessagesToReceive` = **20** per
  `MaximumWaitTime` = **5s** (`AzureServiceBusEndpoint.cs:53,61`), hardcoded 250ms idle sleep.
- **The batched listener hands the receiver `Envelope[]`** (`BatchedAzureServiceBusListener.cs:160`)
  → durable mode uses the batched multi-VALUES inbox insert. Good. Settlement, however, is
  **per-message** `CompleteMessageAsync` through a concurrency-1 RetryBlock
  (`BatchedAzureServiceBusListener.cs:44-45`, `DurableReceiver.cs:660-664`) — 20 messages per
  receive, 20 settlement round trips, serialized.
- **PrefetchCount just landed** (GH-3471 / #3488, commit `a53be88c5`): endpoint + transport-wide
  default, applied to processor/receiver/session-receiver options
  (`AzureServiceBusEndpoint.cs:39,72-85`, `AzureServiceBusTransport.Listening.cs:150-187`).
  Default remains 0. **Unreleased — this plan's Wave 1 doubles as its pre-release validation.**
- **Inline mode never sets `MaxConcurrentCalls`** → SDK default **1** concurrent handler per
  processor; only reachable via `ConfigureProcessor`. Inline ASB is single-threaded per endpoint
  out of the box (RabbitMQ-analogous). Also `AutoCompleteMessages` is left at SDK default *true*
  with a structural double-settlement risk when the first settle attempt fails and falls to the
  background retry block.
- **Sessions are hand-rolled and quadratic**: `RequireSessions(n)` sets `ListenerCount`
  (`AzureServiceBusQueueListenerConfiguration.cs:170-182`), ListeningAgent builds n session
  listeners (`ListeningAgent.cs:365-375`), and **each** spawns n accept loops
  (`SessionSpecificListener.cs:36-47`) → **n² concurrent `AcceptNextSessionAsync` loops**.
  Each accepted session does ONE `ReceiveMessagesAsync` batch, processes sequentially inline,
  then disposes the session receiver (lock churn + AMQP link create/teardown per batch,
  `SessionSpecificListener.cs:84-88,226-228`). No `ServiceBusSessionProcessor`, no
  `MaxConcurrentSessions`/`MaxConcurrentCallsPerSession` (MassTransit has both).
- **No lock renewal on the batched path** (zero `RenewMessageLockAsync` hits): messages queued
  behind `MaxDegreeOfParallelism` workers hold locks that silently expire; `CompleteAsync`
  swallows lock-invalid errors (`AzureServiceBusEnvelope.cs:43-51`) → quiet redelivery
  (durable dedups via inbox; buffered already completed-on-receipt). Inline's remedy is
  `MaxAutoLockRenewalDuration` via `ConfigureProcessor` (SDK default 5 min).
- **Sender**: real `ServiceBusMessageBatch` + `TryAddMessage` with size-limit handling and
  partial-failure accounting (`AzureServiceBusSenderProtocol.cs:55-111`) — the best batch
  protocol of the four transports — but behind the 250ms debounce and
  `MessageBatchMaxDegreeOfParallelism` = 1. **Inline sender = `SendMessageAsync` per envelope**
  (`InlineAzureServiceBusSender.cs:46-52`) and it backs EVERY requeue/defer/retry path.
  Partitioned entities: per-batch LINQ `GroupBy` on SessionId + fail-fast on first failed group
  (`AzureServiceBusSenderProtocol.cs:113-185`).
- **Back-pressure stop** closes the receiver/processor; client-prefetched undelivered messages
  drop with locks left to expire → DeliveryCount increments every cycle. With PrefetchCount now
  real, aggressive prefetch + small `BufferingLimits` can push messages toward MaxDeliveryCount
  dead-lettering (the new XML docs warn about prefetch-vs-lock aging).
- Mapper: `Body.ToArray()` copy per message, all `ApplicationProperties` copied + reserved
  headers double-read, `Values.ToArray()` per outgoing message (`AzureServiceBusEnvelopeMapper.cs:27-64`,
  `EnvelopeMapper.cs:391-397`); `GroupId ↔ SessionId` is automatic (`:50`).

## 1. Theories of overhead, ranked

- **A1 (HIGH): receive ceiling ≈ 20/RTT per listener with prefetch 0.** Prediction: the new
  PrefetchCount is the single biggest throughput lever on ASB; Wave 1 produces the numbers that
  justify (a) shipping it and (b) a recommended default guidance (likely 2-3× the batch size,
  bounded by lock duration).
- **A2 (HIGH): per-message settlement serialization** — 20 completes per receive through a
  sequential RetryBlock; completion RTT gates the durable/buffered pipeline the same way SQS
  deletes do. ASB has no batch-settlement API, so the fix is concurrency (AO4), not batching.
- **A3 (HIGH, sessions): n² accept loops + one-batch-per-accept churn.** Prediction: session
  throughput is dramatically below both non-session ASB and MassTransit's session processor;
  fixing the quadratic alone is a headline win, moving to `ServiceBusSessionProcessor` is the
  real fix.
- **A4 (MEDIUM): inline = MaxConcurrentCalls 1** — same "slow by default" story as RabbitMQ
  inline; docs + surfaced knob.
- **A5 (MEDIUM): lock expiry under load** (no renewal on batched path) — shows up as duplicate
  executions and DeliveryCount climb rather than latency; measure duplicate rate vs
  `MaxDegreeOfParallelism` × handler time vs lock duration.
- **A6 (MEDIUM): inline sender per-message sends** on requeue/defer/retry paths — failure-heavy
  workloads pay per-message RTTs (plus defer semantics differ by listener: batched re-sends
  WITHOUT completing the original — double-delivery window).
- **A7 (LOW-MEDIUM): sender debounce + 1 in-flight batch** (#3490 T1 family) and the
  partitioned-entity GroupBy/fail-fast path.
- **A8 (LOW): mapper allocations** — quantify in the shared microbench suite; fixes shared with
  Kafka O4.

## 2. Harness

`TransportPerfRig` ASB adapter + native twin on raw `Azure.Messaging.ServiceBus`
(`ServiceBusProcessor` with tuned `MaxConcurrentCalls`/prefetch — the twin represents the SDK
used *well*). **Broker: a real ASB namespace, Standard AND Premium tiers** — the local emulator
is not representative (known environmentally flaky in this repo's test history; sessions and
scheduling misbehave) and throughput quotas differ by tier. Record namespace tier + region per
run. Sandbox: dedicated queue prefix + auto-teardown; ASB Standard base cost is pennies per
million ops.

## 3. Experiment matrix

Baseline: non-session queue, Buffered, defaults (20/5s receive, prefetch 0, batch-send
100/250ms/1), 1Kb payloads, ~10ms handler.

| # | Experiment | Theory | Levers |
|---|---|---|---|
| AE1 | Mode sweep: Buffered / Durable(PG) / Inline(default) / Inline+MaxConcurrentCalls=20 | A2,A4 | mode, `ConfigureProcessor` |
| AE2 | **PrefetchCount sweep: 0/20/60/200** per mode — the pre-release validation | A1 | new `PrefetchCount()` |
| AE3 | Settlement concurrency prototype (AO4) vs serialized completes | A2 | branch build |
| AE4 | Receive shape: MaximumMessagesToReceive 10/20/50 × MaximumWaitTime 1/5s | A1 | endpoint config |
| AE5 | Sessions: current listener n=1/3/5 (observe n² loops) vs post-AO2-fix vs `ServiceBusSessionProcessor` prototype; vs MassTransit same-box reference | A3 | branch builds |
| AE6 | Lock-expiry stress: handler 10s × MDOP 20 × lock 30s → duplicate rate | A5 | queue config |
| AE7 | Send: MessageBatchMaxDegreeOfParallelism 1/4/8 × debounce 250/50ms; partitioned entity on/off | A7 | endpoint config |
| AE8 | Failure path: 5% failures → requeue/defer churn (batched vs inline listener defer semantics) | A6 | error policy |
| AE9 | Back-pressure cycle with prefetch 200: DeliveryCount climb + DLQ drift | A1×A5 | `BufferingLimits` |
| AE10 | Mapper: default vs minimal; 100Kb payloads; Std vs Premium (1MB) | A8 | `UseInterop`, tier |
| AE11 | Sequencing shapes (§4) | — | see below |

Output/archival rules identical to #3490.

## 4. Sequencing / GlobalPartitioning-equivalents

ASB is the one transport with a first-class broker sequencing primitive Wolverine already maps:
**sessions** (`GroupId ↔ SessionId` automatic). Shapes to benchmark:
1. **Sessions (fixed listener) + inline or `PartitionProcessingByGroupId`** — note today's
   buffered path completes on enqueue and releases the session lock after the batch, so strict
   per-session serial execution requires the pairing; document loudly.
2. **Non-session queue + `PartitionProcessingByGroupId(slots)`** — consumer-side only, no
   session tax; likely the throughput winner when cluster-wide exclusivity isn't needed.
3. **`UseShardedAzureServiceBusQueues(base, N)`** (`AzureServiceBusTransportExtensions.cs:498-509`)
   — N plain queues + Wolverine hash routing (forced Durable inside `GlobalPartitioned`);
   quantify vs sessions.
Deliverable: sessions-vs-sharding-vs-local-partitioning decision table — this is also the
competitive-positioning story vs MassTransit's session support (capability-doc crossover:
richer session surface is a named opportunity there).

## 5. Optimization backlog (gated on matrix confirmation)

- **AO1 (A3): rewrite the session listener on `ServiceBusSessionProcessor`** with
  `MaxConcurrentSessions`/`MaxConcurrentCallsPerSession` surfaced. Fixes churn, the n²
  explosion, lock retention, and closes the MT feature gap in one move.
- **AO2 (A3, small + immediate): de-quadratic the current session listener** (spawn the accept
  loops once, not per ListenerCount instance) — shippable before AO1.
- **AO3 (A4): surface `MaxConcurrentCalls`** on inline endpoints (+ align `AutoCompleteMessages`
  handling to remove the double-settlement window).
- **AO4 (A2): parallel settlement** — widen the complete-block concurrency for ASB (settlement
  order doesn't matter; locks are per-message).
- **AO5 (A5): lock renewal on the batched path** (bounded renewal while in local queue), or a
  documented sizing rule (BufferingLimits × handler time < lock duration) enforced with a
  startup warning.
- **AO6 (A1): PrefetchCount guidance + defaults** from AE2 data; ship in the same release as
  #3488 so the feature lands with numbers.
- **AO7 (A8): mapper fixes** (shared with Kafka O4).
- **AO8 (A6): batched-listener defer should settle the original** (complete-then-resend like
  inline does) — correctness alignment.

## 6. Measured-wins ledger (for release notes / blog posts)

Same rules as #3490 §9: archived runs only (namespace tier + region recorded), same-rig
before/after, release-note phrasing; negative results logged below. **AE2's prefetch numbers
feed the #3488/GH-3471 release notes directly** — that's the first ledger entry to chase.

| Optimization | PR | Scenario (AE-cell) | Metric | Before | After | Release-note one-liner |
|---|---|---|---|---|---|---|
| _(PrefetchCount validation (#3488), AO2 session de-quadratic, AO4 settlement concurrency, ... — rows added as measured)_ | | | | | | |

## 7. Sequencing & exit criteria

- **Wave 0 (now)**: rig adapter + native twin; AO2 (de-quadratic) and AO8 (defer settle) are
  small enough to land from code review + existing tests, pre-box.
- **Wave 1 (box + real namespace)**: AE1-AE4 + AE2 prefetch validation; dotTrace on
  AE1-durable and AE5-current.
  Exit: prefetch numbers ready for the #3488 release notes; A1/A2 confirmed or killed.
- **Wave 2**: AE5-AE9 sessions + stress cells; sequencing decision table.
- **Wave 3**: AO1/AO3/AO4/AO5 landed + re-measured; ledger filled.
- **Wave 4**: update `docs/guide/messaging/transports/azureservicebus/performance.md` (seeded
  2026-07-18 with qualitative guidance; prefetch subsection badged 6.21) with measured numbers +
  release notes/blog from the ledger.

## 8. Risks / honesty notes

- Emulator numbers are never publishable; sessions/scheduling don't behave there (known local
  flake history — CI/real namespace is the signal).
- Standard vs Premium changes message size limits, throughput units, and latency floors —
  every ledger row states the tier.
- Session cells depend on group-count distribution; state it per cell.
- PrefetchCount is unreleased: AE2 findings may change its defaults/docs before it ships —
  that's the point, but sequence the release accordingly.
