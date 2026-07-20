# Wolverine's Kafka Transport Just Grew Up

*Draft — published as Wolverine 6.13.0 on 2026-06-18.*

Wolverine has supported Kafka for a while, but until yesterday the integration carried a few embarrassing seams: every consumed message paid for a synchronous `Commit()` round-trip to the broker, there was no first-class way to scale a single partition's processing, replay was a "stop the world" affair, and the non-blocking retry pattern everyone copies from Spring/Uber simply wasn't there. We tracked all of this under the [#3134 "Re-Evaluate Kafka Integration"](https://github.com/JasperFx/wolverine/issues/3134) umbrella, and on **June 18 we shipped nine merged PRs against it in a single day**. Here's the tour.

---

## The headline: a ~20x throughput cliff is gone

The original Wolverine Kafka listener called `_consumer.Commit()` — argument-less, blocking — after every successfully handled message. That is a synchronous network round-trip per message, and on a busy partition it dominated the cost of consuming. Internal benchmarks pegged the overhead at roughly **20× slower than the idiomatic Kafka model**.

[**#3150** — *Kafka: commit-strategy overhaul with `CommitMode`*](https://github.com/JasperFx/wolverine/pull/3150) replaces that with an explicit, opt-in strategy that defaults to the idiomatic non-blocking path:

```csharp
opts.UseKafka(connectionString)
    .ConfigureListeners(l => l.CommitOffsets(CommitMode.StoreThenAutoFlush));
```

The four modes:

| Mode | What it does | When to reach for it |
|---|---|---|
| `StoreThenAutoFlush` *(default)* | `EnableAutoOffsetStore=false` + `StoreOffset` per completed message; Kafka's background committer flushes on `AutoCommitIntervalMs` | The new default — idiomatic Kafka throughput |
| `PerMessage` | Synchronous commit of the message's own offset | Strict at-least-once on low-volume topics |
| `BatchCount(n)` | Commit watermark every N messages | High-volume topics where you want a tunable lever |
| `BatchInterval(t)` | Commit watermark every T elapsed | Bursty traffic |

A subtle but important correctness fix rides along: `CompleteAsync` and the DLQ paths now commit the message's **specific `TopicPartitionOffset`** (offset + 1), not the consumer's global position. That was a prerequisite for every concurrency feature below.

If you'd already set `EnableAutoCommit=true` on the Kafka client, Wolverine now respects that and issues no manual commits at all — the previous transport blanket-overrode it.

### And: in-flight-safe watermarks for every mode

In Wolverine's default buffered listener (handlers running at `MaxDegreeOfParallelism`), messages can complete out of order. The original Batch strategy tracked an in-flight watermark; the new `StoreThenAutoFlush` and `PerMessage` strategies initially did not, which meant a fast-completing offset 11 could advance the committed position past a still-in-flight offset 10 — and on a crash, that 10 would be silently dropped.

[**#3161** — *in-flight-safe offset watermark for all commit strategies*](https://github.com/JasperFx/wolverine/pull/3161) routes all three manual strategies through a per-partition `OffsetWatermark`. The committable position is now the lowest still-in-flight offset, or high-water + 1 when nothing is in flight. **It never advances past in-flight work**, it's monotonic across re-seeks, and it tolerates the offset gaps that compacted or `read_committed` transactional topics produce.

---

## Scale-out, the way Kafka actually wants you to do it

The next two PRs make Kafka's own group coordinator the recommended path to scale Wolverine handlers across nodes.

### [**#3139** — Cooperative-sticky rebalancing + static membership](https://github.com/JasperFx/wolverine/pull/3139)

Two opt-in knobs that any production Kafka deployment will recognize:

```csharp
opts.UseKafka(connectionString)
    .UseCooperativeStickyAssignment()  // incremental rebalances
    .UseStaticMembership();             // POD_NAME → HOSTNAME → machine name
```

- **`UseCooperativeStickyAssignment()`** sets `partition.assignment.strategy = CooperativeSticky`, so a rebalance only moves the partitions that *need* to move — the rest of the group keeps working uninterrupted.
- **`UseStaticMembership()`** sets `group.instance.id` so a rolling restart of the same pod doesn't churn the partition map. Instance id is resolved from `POD_NAME` → `HOSTNAME` → machine name (the k8s StatefulSet idiom), and Wolverine logs the resolved id at startup so you can verify per-node uniqueness.

Both are opt-in so you don't break a live rolling upgrade by silently switching assignment strategies. The Kafka docs section now spells out the two-step rolling onto cooperative-sticky.

### [**#3140** — Opt-in intra-partition concurrency by key](https://github.com/JasperFx/wolverine/pull/3140)

The second concurrency lever. Within a single partition assigned to your node, process messages with **different keys concurrently while preserving strict ordering per key**:

```csharp
opts.ListenToKafkaTopic("orders")
    .ProcessConcurrentlyByKey(PartitionSlots: 8);
```

The trick is that this reuses Wolverine's existing durable sharded execution — it forces the durable inbox, persists each envelope in consumption order, commits the Kafka offset on persist (the specific-offset fix from #3150), and shards inbox processing by the message key. The inbox is the reliability boundary, so a crash or rebalance can't lose in-flight work.

### [**#3146** — First-class `AutoOffsetReset` + ephemeral hot-tail](https://github.com/JasperFx/wolverine/pull/3146)

Cold start and live-tail consumption are now first-class:

```csharp
opts.ListenToKafkaTopic("metrics").BeginAtEarliest();   // or .BeginAtLatest()
opts.ListenToKafkaTopic("events").TailFromLatest();     // broadcast/fan-out
```

`TailFromLatest()` is the interesting one — the listener joins a **unique per-process consumer group** (`{ServiceName}-hot-tail-{guid}`) at the tail with `EnableAutoCommit=true`. Every node receives every message, no commits, no replay. This is the Kafka-local equivalent of a broadcast subscription, and it's perfect for cache invalidation, ephemeral notifications, or dashboards. The trade-off (throwaway consumer groups left behind on the broker) is called out in the docs.

---

## Replay, finally as a discrete operation

[**#3147** — *Bounded one-shot replay via `Assign`*](https://github.com/JasperFx/wolverine/pull/3147) lets you replay a window of a topic's history back through the normal Wolverine handler pipeline **without disturbing the live consumer group**:

```csharp
// Programmatic
await host.ReplayKafkaTopicAsync(new KafkaReplayRequest
{
    Topic = "orders",
    FromTimestamp = DateTimeOffset.UtcNow.AddHours(-2),
});
```

```bash
# CLI
dotnet run -- kafka-replay orders --from-timestamp 2026-06-18T10:00:00Z
```

Under the covers, `KafkaReplay` spins up a throwaway `Assign()`-based consumer with a unique group id and `EnableAutoCommit=false`, resolves per-partition start/end from explicit offsets or `OffsetsForTimes`, seeks to the start, and feeds every record through `runtime.Pipeline.InvokeAsync` — the **same** envelope mapping and handlers as live consumption. Each partition pauses at its end boundary. The live group's committed offsets are untouched.

Live seek of a running group-subscribed listener and a CritterWatch control pane are explicit follow-ups.

---

## Non-blocking tiered retries — the Spring/Uber pattern, native

This is the one a lot of users have been asking for. [**#3148** — *Non-blocking tiered retry topics via `OnException` DSL*](https://github.com/JasperFx/wolverine/pull/3148):

```csharp
opts.OnException<TransientException>()
    .MoveToKafkaRetryTopic(1.Seconds(), 30.Seconds(), 5.Minutes());
```

On a matching failure the message is produced to a tiered fixed-delay retry topic (`{source}.retry.{delay}`), the source offset is committed so the partition **keeps flowing — no head-of-line blocking**, and a delayed consumer reprocesses it through the normal handler pipeline once the tier delay elapses. After the last tier it lands in the existing Kafka DLQ. Tier, attempt, and exception metadata travel in headers.

Two design notes worth calling out:

- The continuation **self-guards**: if a non-Kafka listener somehow hits this rule it falls back to a normal inline retry, so the policy can never cross transports. The Kafka transport scans `opts.Policies.Failures` at `ConnectAsync` and warns at startup if non-Kafka listeners are present.
- The core got one small generic hook — `IFailureActions.ContinueWith(IContinuationSource)` — so transport-specific continuations can plug into the standard error DSL discoverably. This was the gap Pulsar's resiliency support had to work around; that pattern is now first-class.

---

## Exactly-once building blocks (the cheap ones)

[**#3149** — *Idempotent producer + `read_committed` + EOS docs*](https://github.com/JasperFx/wolverine/pull/3149) ships the cheap, opt-in pieces and — just as importantly — documents Wolverine's actual exactly-once story so you reach for the right tool:

```csharp
opts.UseKafka(connectionString)
    .UseIdempotentProducer()  // producer→broker dedupe
    .UseReadCommitted();      // skip records from aborted Kafka txns
```

The new docs section leads with the **durable inbox/outbox as the recommended path** for DB-backed apps — that's effectively-once across DB + Kafka, which Kafka transactions can't span — then covers the idempotent producer, `read_committed`, the handler-idempotency reality, and a clear non-goal callout pointing DB-free Kafka→Kafka EOS users at Kafka Streams.

A transactional read-process-write EOS engine remains an explicit non-goal for Wolverine.

---

## One small but annoying bug

[**#3151** — *Fix `ExtendConsumerConfiguration` inheritance*](https://github.com/JasperFx/wolverine/pull/3151), contributed by [@Ferchke7](https://github.com/Ferchke7): a regression from a recent PR where `ExtendConsumerConfiguration()` created an empty topic-level `ConsumerConfig`, which Kafka then preferred over the parent, silently dropping any global consumer settings configured via `UseKafka(...).ConfigureClient(...)`. Now the topic config is layered properly: parent → existing topic → extension callback.

---

## Where we are vs. where this leaves the .NET Kafka story

A year ago you would reasonably have looked at the Wolverine Kafka transport and concluded that the .NET story for Kafka tops out at "manual `confluent-kafka-dotnet`." After yesterday, Wolverine has:

- ✅ Idiomatic non-blocking commits, four selectable strategies, in-flight-safe watermarks
- ✅ Native scale-out via cooperative-sticky + static membership
- ✅ Second-tier concurrency by message key within a partition
- ✅ First-class cold-start / hot-tail consumption
- ✅ Bounded replay through the normal handler pipeline, without touching the live group
- ✅ Non-blocking tiered retry topics wired into the standard error DSL
- ✅ Idempotent producer + `read_committed` + an honest EOS story built on the durable inbox/outbox

The remaining gap the umbrella tracks is the transactional read-process-write EOS engine — explicitly a non-goal — and live seek of a running group-subscribed listener, which is a near-term follow-up. Everything else on the original re-evaluation list shipped yesterday.

Upgrade to **Wolverine 6.13.0** (or 6.13.1 for the unrelated RDBMS DLQ fix that followed), tweak nothing, and you'll already see the throughput bump from the new default commit strategy. Then pick the levers that match your topic shape.

---

*— Jeremy*

<!--
DRAFT NOTES (remove before publishing):
- Verify the "~20x" framing — it comes from the #3134 umbrella issue summary; double-check numbers before posting publicly.
- Confirm we want to call out the EOS "non-goal" this directly, or soften.
- Decide whether to land this on jasperfx.net first or as a critter-stack-week roundup.
- Consider adding a "minimum upgrade" sample that combines #3139 + #3140 + #3150 in one block.
-->
