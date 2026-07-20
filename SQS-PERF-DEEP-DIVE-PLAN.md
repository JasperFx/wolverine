# Amazon SQS Performance Deep-Dive Plan (2026-07-18)

Goal: measure Wolverine-over-SQS overhead vs the raw AWS SDK across endpoint modes, fix the
bug-shaped findings uncovered during research, produce throughput-tuning guidance, and land
confirmed optimizations. Companion/umbrella: `KAFKA-PERF-DEEP-DIVE-PLAN.md` (wolverine#3490) —
shared metrics semantics, resolution-chain costs, BatchedSender mechanics, and back-pressure
behavior live there and are not restated.

All file:line cites verified against `main` @ 6.20.0.

---

## 0. Transport-specific facts that frame everything

- **SQS is the good citizen on receive**: one `ReceiveMessageAsync` returns up to
  `MaxNumberOfMessages` = **10** (`AmazonSqsQueue.cs:107`) with long polling ON by default
  (`WaitTimeSeconds` = 5, `:100`), and the listener hands the receiver a real **`Envelope[]`**
  (`SqsListener.cs:112-115`) → durable mode DOES use the batched multi-VALUES inbox insert
  (`DurableReceiver.cs:608-668`). The Kafka/RabbitMQ per-message-insert gap does not exist here.
- **…but per-message on delete**: completion = one `DeleteMessageAsync` HTTP round trip per
  message (`SqsListener.cs:191-194`); `DeleteMessageBatch` is **never used** (zero hits
  repo-wide). 10 delete RTTs per 1 receive RTT, flowing through a sequential RetryBlock. This
  is SQS's analogue of Kafka's per-message flush — the presumptive #1 ceiling.
- **Send batching is real but throttled by defaults**: `SqsSenderProtocol` chunks by 10 and uses
  `SendMessageBatchAsync` (`SqsSenderProtocol.cs:38-46`); `MessageBatchSize` is overridden to 10
  (`AmazonSqsQueue.cs:44`) but `MessageBatchMaxDegreeOfParallelism` default **1**
  (`Endpoint.cs:281`) caps sustained sends at ~10 msgs per SQS RTT per endpoint, behind the
  250ms debounce (#3490 T1).
- **Correctness bug, fix regardless of perf**: `SendMessageBatchResponse.Failed` is never
  inspected — per-entry failures (throttling, oversize) are silently marked successful
  (`SqsSenderProtocol.cs:42-48`; the purpose-built `OutgoingSqsBatch.TryGetEnvelope` at
  `:119-122` is dead code). Silent message loss under throttling.
- **Wire format**: default mapper embeds the whole serialized envelope **base64** in the body
  (`ISqsEnvelopeMapper.cs:31-33`) — ~33% inflation against the 256KB limit + 3 large
  allocations per message per direction. Only one message attribute is used, so the SQS
  10-attribute cap is a non-issue.
- **No visibility-timeout renewal**: no `ChangeMessageVisibility` anywhere; `VisibilityTimeout`
  fixed at receive (default 120s, `AmazonSqsQueue.cs:29,81-92`). Handlers (or buffered backlog
  dwell) past 120s → redelivery; durable dedups via the inbox, buffered already deleted
  (delete-on-receive, `BufferedReceiver.cs:219-228` — the #3137 loss window).
- **Native `DelaySeconds` scheduling SHIPPED** (#3472): `IConditionalNativeScheduling`, ≤900s on
  standard queues (`SqsSenderProtocol.cs:11,28-31`, `AmazonSqsQueue.cs:259-272`) — the
  capability-research doc's "DelaySeconds unused" note is stale; update it.
- Requeue/defer = delete + un-batched inline re-send (2 API calls, `SqsListener.cs:47-55`);
  count-only batch chunking with no byte-size accounting (whole-request bounce risk on large
  batches); no SQS-specific throttle handling beyond the generic 100ms×n backoff (cap 1s).

## 1. Theories of overhead, ranked

- **S1 (HIGH): per-message delete round trips.** Prediction: receive-side throughput ceiling
  ≈ concurrency-limited delete RTTs, not receive RTTs; batching deletes (SO1) is the biggest
  win. LocalStack RTTs (~1-3ms) will understate this badly vs real SQS (~5-15ms + TLS) —
  see harness note.
- **S2 (HIGH): send path = 10-per-RTT × 1 in-flight × 250ms debounce.** Prediction: raising
  `MessageBatchMaxDegreeOfParallelism` is the single cheapest user-side send-throughput lever;
  tuning guidance must lead with it.
- **S3 (MEDIUM): base64 + double serialization** — CPU/alloc cost and wire inflation; matters
  most at 100Kb+ payloads where inflation forces claim-check territory.
- **S4 (MEDIUM): poll-loop shape** — single poller per endpoint (`ListenerCount` default 1),
  250ms idle sleep, `WaitTimeSeconds`=5. Prediction: `ListenerCount` scaling is near-linear
  until delete RTTs saturate; longer wait (20s) cuts empty-receive costs at low traffic.
- **S5 (MEDIUM): visibility-timeout interplay** — back-pressure stop + 120s reappearance,
  buffered delete-on-receive, no renewal for long handlers. Mostly a correctness/duplicates
  story; measure duplicate rates under overload, not just latency.
- **S6 (LOW-MEDIUM): FIFO throughput** — FIFO caps (300 tps/group, 3000 batched without
  high-throughput mode), no high-throughput-FIFO helper (`FifoThroughputLimit` unexposed;
  reachable only via raw `ConfigureQueueCreation`), null-GroupId FIFO sends rejected with no
  fallback. Matters only for FIFO users but the sequencing story (§4) runs through it.

## 2. Harness

Same `TransportPerfRig` adapter pattern as the Kafka/RabbitMQ plans; native twin on raw
`AmazonSQSClient` (`ReceiveMessageAsync` batch 10 + `DeleteMessageBatchAsync` — twin should use
the *optimal* native pattern, since that's the ceiling we're chasing).

**Broker fidelity is THE risk for SQS**: LocalStack (compose port 4566) has toy latencies and no
real throttling. Protocol: develop + smoke on LocalStack, but **run all measured cells against a
real SQS account** (dedicated queues, same region as the box's egress; record region + observed
base RTT per run). Budget note: SQS API calls are ~$0.40-0.50/million requests — a full matrix
run is dollars, not real money; batching experiments literally measure the cost savings too
(delete batching cuts the AWS bill 10×, a release-note-friendly angle).

## 3. Experiment matrix

Baseline: standard queue, Buffered, defaults (10/5s receive, 120s visibility, batch-send 10/250ms/1).

| # | Experiment | Theory | Levers |
|---|---|---|---|
| QE1 | Mode sweep: Buffered / Durable(PG) / Inline | S1,S5 | endpoint mode |
| QE2 | Delete batching prototype (SO1) vs per-message | S1 | branch build |
| QE3 | Send: MessageBatchMaxDegreeOfParallelism 1/4/16 × MessageBatchTimeout 250/50/10ms | S2 | endpoint config |
| QE4 | Poller scaling: ListenerCount 1/4/8 × WaitTimeSeconds 1/5/20 | S4 | listener config |
| QE5 | Payload: 1Kb/100Kb/200Kb (near-limit) × default vs RawJson mapper | S3 | `ISqsEnvelopeMapper` |
| QE6 | Overload burst 2×: duplicate-rate + back-pressure + visibility interplay | S5 | `BufferingLimits`, visibility |
| QE7 | FIFO: groups sweep (10/100/1000 groups) × with/without high-throughput attrs | S6 | raw queue attributes |
| QE8 | Batch-send failure injection (throttle simulation): loss measurement pre/post SO2 fix | S4-bug | branch build |
| QE9 | Sequencing shapes (§4) | — | see below |

Output/archival rules identical to #3490.

## 4. Sequencing / GlobalPartitioning-equivalents

SQS has a real broker-native primitive: **FIFO message groups** (sequential per `MessageGroupId`,
parallel across groups; Wolverine already maps `Envelope.GroupId` → `MessageGroupId`,
`ISqsEnvelopeMapper.cs:24`). But nothing consumer-side respects group ordering across a
10-message receive array today — buffered/durable execute the array concurrently. Shapes to
benchmark:
1. **FIFO groups + `PartitionProcessingByGroupId(slots)`** — broker ordering + consumer-side
   per-group serialization. The natural GP-equivalent; verify end-to-end ordering under
   `MaxNumberOfMessages`=10 and document the required pairing (FIFO alone is NOT enough).
2. **Standard queue + `PartitionProcessingByGroupId`** — consumer-only sequencing, no FIFO tax.
3. **`UseShardedAmazonSqsQueues(base, N)`** (`AmazonSqsTransportExtensions.cs:231-243`) — N
   standard queues + Wolverine hash routing (forced Durable inside a `GlobalPartitioned`
   topology — quantify that tax).
4. **Fair queues** (`EnableFairQueueMessageGroups`) — tenant fairness only, no ordering; include
   one cell to characterize its cost since we're ahead of every competitor on it.
Deliverable: ordering-guarantee × throughput × cost decision table for the docs.

## 5. Optimization backlog (gated on matrix confirmation)

- **SO1 (S1): `DeleteMessageBatchAsync` coalescing** — accumulate receipt handles (N=10 or
  T=50-100ms window) behind the complete block; also batches the requeue-path deletes. Cuts
  API calls (and AWS spend) ~10× on the receive side.
- **SO2 (S4-bug, do first regardless): inspect `SendMessageBatchResponse.Failed`** and route
  per-entry failures through `TryGetEnvelope` → sender callback retry. Correctness fix;
  release-note headline material.
- **SO3: size-aware batch chunking** (respect the 256KB/1MiB request cap, count + bytes).
- **SO4 (S2): raise `MessageBatchMaxDegreeOfParallelism` default for SQS** (or at least loud
  docs); consider trimming the debounce for SQS where batches are capped at 10 anyway.
- **SO5 (S5): `ChangeMessageVisibility` renewal** for in-flight messages (durable/buffered
  backlog + long handlers), or at minimum a documented BufferingLimits-vs-visibility sizing rule.
- **SO6 (S6): high-throughput FIFO helper** (`FifoThroughputLimit`/`DeduplicationScope`
  first-class) + explicit error on null-GroupId FIFO sends.
- **SO7 (S3): claim-check / payload offloading story** for >256KB (capability-doc crossover;
  SQS max is now 1MiB — verify SDK support and update docs either way).

## 6. Measured-wins ledger (for release notes / blog posts)

Same rules as #3490 §9: rows only from archived measured runs (real SQS, region recorded),
before/after on the same rig version, release-note phrasing. **Also record API-call counts per
1k messages** — for SQS, "10× fewer AWS API calls" is a cost win worth quoting alongside
latency. Log negative results below the table.

| Optimization | PR | Scenario (QE-cell) | Metric | Before | After | Release-note one-liner |
|---|---|---|---|---|---|---|
| _(SO1 delete batching, SO2 batch-failure handling, SO4 send parallelism, ... — rows added as measured)_ | | | | | | |

## 7. Sequencing & exit criteria

- **Wave 0 (now)**: rig adapter + native twin; LocalStack smoke; SO2 correctness fix can land
  immediately (test via QE8 failure injection, no perf box needed).
- **Wave 1 (box + real SQS)**: QE1-QE5 baseline sweeps; dotTrace on QE1-durable.
  Exit: delete-RTT ceiling quantified; S1/S2 confirmed or killed.
- **Wave 2**: QE6-QE9; sequencing decision table.
- **Wave 3**: SO1/SO3/SO4 landed + re-measured; ledger filled.
- **Wave 4**: update `docs/guide/messaging/transports/sqs/performance.md` (seeded 2026-07-18
  with qualitative guidance) with measured numbers + release notes from the ledger.

## 8. Risks / honesty notes

- LocalStack numbers are NOT publishable — real-SQS runs only for the ledger.
- Real-SQS latency varies by region/network; record base RTT per run and prefer ratios.
- FIFO cells must state group-count assumptions; FIFO throughput is per-group.
- The rig's AWS credentials/queues must be sandboxed (dedicated prefix, auto-teardown).
