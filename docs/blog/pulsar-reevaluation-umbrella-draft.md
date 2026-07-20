<!--
FILED 2026-06-22 as live GitHub issues:
  Umbrella: https://github.com/JasperFx/wolverine/issues/3176
  Children: #3186 (PUL1), #3177 (PUL2), #3178 (PUL3), #3179 (PUL4), #3180 (PUL5),
            #3181 (PUL6), #3182 (PUL7), #3183 (PUL8), #3184 (PUL9), #3185 (PUL10)
This file is the original draft, kept for reference; the canonical version is #3176.
-->

<!--
DRAFT umbrella issue for GitHub — mirrors #3134 "Re-Evaluate Kafka Integration".
Not a blog post; parked here next to the Kafka overhaul draft for review.
Child-issue numbers are placeholders (PUL-n) until the real issues are filed.
-->

# Re-Evaluate Pulsar Integration

> **Umbrella / epic issue.** Tracks a re-evaluation of Wolverine's Apache Pulsar
> integration so it embraces Pulsar idioms (typed schemas, negative-ack, the
> Reader interface, multi-topic subscriptions) and reaches parity with the
> operational maturity the Kafka transport gained under #3134. The detailed
> current-state audit (with file refs) and cross-framework comparison are in the
> analysis section at the bottom of this issue.

The Kafka transport just went through a nine-PR overhaul (#3134, shipped in
6.13.0) that turned it from "shaped like the RabbitMQ transport" into something
that embraces Kafka idioms. The Pulsar transport never got the equivalent pass.
It's a solid DotPulsar wrapper — four subscription types, native retry-letter +
dead-letter topics, scheduled send, tenants/namespaces, CloudEvents interop —
but it lags both idiomatic Pulsar and Wolverine's own Kafka transport on schema
support, acknowledgment semantics, replay/broadcast, and consumer/producer
tunability.

## Child issues (the plan)

| # | Issue | Notes / dependencies |
|---|-------|----------------------|
| PUL-1 | **Finish the DLQ sender + resolve transport-vs-endpoint default TODOs** | Closes the `PulsarEndpoint.cs:118` DLQ-sender stub and the `PulsarTransport.cs:31` transport-level-default TODO. **Foundational, do first** — the DLQ paths are half-wired today. |
| PUL-2 | **Negative acknowledgment + redelivery backoff** (`nack`, nack-redelivery-backoff, ack-timeout-backoff) | Independent, cheap. The single most idiomatic Pulsar primitive currently missing — today we only `Acknowledge` + `RedeliverUnacknowledgedMessages`. |
| PUL-3 | **Subscription initial position** (`Earliest`/`Latest`) | Independent, cheap. Direct analogue of Kafka #3146's `BeginAtEarliest/Latest`. |
| PUL-4 | **Per-consumer / per-producer customization hooks** (`ConfigureConsumer`/`ConfigureProducer`) | Independent. Today only the global `IPulsarClientBuilder` is exposed; this unblocks fluent compression/batching tuning and matches the Kafka surface. |
| PUL-5 | **Acknowledgment-strategy choice** (cumulative + batched ack by count/interval) | Pulsar's analogue of Kafka's `CommitMode` overhaul (#3150). After PUL-4. |
| PUL-6 | **Multi-topic & regex/pattern subscriptions** | One DotPulsar consumer over many topics. Analogue of Kafka topic groups; Pulsar supports this natively. |
| PUL-7 | **Align retry-letter-topic DSL** (`MoveToPulsarRetryTopic`, non-blocking, discoverable) | Mirrors Kafka #3148. Build as a standard error policy via `IHandlerPolicy`/chain-config, startup-validate + warn on non-Pulsar endpoints, never cross-transport. Also document `Key_Shared` as the recommended by-key concurrency path (free analogue of #3140). |
| PUL-8 | **Pulsar Schema support** (JSON → Avro → `AUTO_CONSUME`) | **The big one.** Closes the gap with both Kafka's schema registry and Spring Pulsar. Likely a schema-aware producer/consumer redesign around typed `IProducer<T>`/`IConsumer<T>`. |
| PUL-9 | **Reader interface** → bounded replay (`ReplayPulsarTopicAsync`) + non-durable broadcast / hot-tail | Combined analogue of Kafka #3147 (replay) and #3146 (hot-tail). Doesn't disturb live durable subscriptions. |
| PUL-10 | **Producer dedup + Pulsar transactions** | Analogue of Kafka #3149's EOS building blocks. Lowest priority — Pulsar txns are heavier and lower-demand. |

## Declared non-goals (ported from #3134, Pulsar-adjusted)

- **Cooperative-sticky rebalancing + static membership (#3139 analogue).** N/A — the Pulsar broker owns subscription/partition assignment. Nothing to build; listed here so it isn't re-raised as a gap.
- **In-flight-safe offset watermark (#3161 analogue).** Largely moot — Pulsar acknowledges per-message-id rather than by a single monotonic partition offset, so the "fast offset 11 advances past in-flight offset 10" hazard doesn't exist the same way. PUL-5 should still confirm cumulative-ack ordering is safe under the buffered listener.
- **Transactional read-process-write EOS engine.** Same stance as Kafka: Wolverine's durable inbox/outbox already gives effectively-once for DB-backed apps. The cheap layer (producer dedup) is in PUL-10; a full Pulsar-transaction read-process-write engine is out of lane.

## Suggested sequencing

1. **PUL-1** (DLQ sender + default TODOs) — finishes half-wired code, lowest risk.
2. **PUL-2** (negative-ack) and **PUL-3** (initial position) — cheap, independent, high idiomatic value.
3. **PUL-4** (consumer/producer hooks) — unblocks tuning and PUL-5.
4. **PUL-5** (ack strategy) and **PUL-6** (multi-topic) — streaming-grade operations.
5. **PUL-7** (retry-topic DSL alignment) — interacts with PUL-1/PUL-2 ack semantics.
6. **PUL-8** (schema), **PUL-9** (Reader/replay/broadcast), **PUL-10** (dedup/txn) — larger / parallelizable; schema is the headline differentiator.

## For implementers (hand-off)

This epic is self-contained — start from this issue. Each child issue should carry its own design decisions, file references, and acceptance criteria.

- **Start with PUL-1**: the DLQ sender at `src/Transports/Pulsar/Wolverine.Pulsar/PulsarEndpoint.cs:118` is a stub, and `PulsarTransport.cs:31` has an open question about transport-level vs per-endpoint DLQ/retry defaults.
- **Independent / parallelizable:** PUL-2, PUL-3, PUL-4, PUL-6, PUL-9.
- **Dependency edges:** PUL-5 after PUL-4; PUL-7 interacts with PUL-1/PUL-2.
- **Client library:** DotPulsar 5.1.2 (`Directory.Packages.props`). Verify its schema, `Reader`, and `nack` API surface before locking PUL-8/PUL-9 estimates.
- **Error-policy convention:** transport-specific error continuations (PUL-7) are built as standard error policies via `IHandlerPolicy`/chain-config, startup-validate + warn on non-matching endpoints, never cross-transport, and the continuation must be discoverable (not an opaque `CustomAction` func).
- **Build/test:** build the full `wolverine.slnx` (not `wolverine_slim.slnx`) before pushing; Pulsar tests require the docker-compose infra (`docker compose up -d pulsar`); prefer `--framework net9.0` for faster single-TFM runs.

---

## Current-state audit (analysis)

### What the Pulsar transport supports today

- **Connection:** `UsePulsar(Action<IPulsarClientBuilder>)` — auth/TLS delegated to DotPulsar's builder.
- **Subscription types:** Exclusive (default), Shared, Failover, Key_Shared — `WithXxxSubscriptionType()`.
- **Scheduled/delayed send:** `SupportsNativeScheduledSend = true` via `MessageMetadata.DeliverAtTime`.
- **Native retry-letter topics:** `RetryLetterQueueing(RetryLetterTopic)` (Shared/Key_Shared only — DotPulsar limitation).
- **Native + Wolverine-storage DLQ:** `DeadLetterQueueing(DeadLetterTopic)` with `Native` / `WolverineStorage` modes.
- **Requeue:** `DeferAsync` re-sends to the source topic; `DisableRequeue()` / `DisablePulsarRequeue()`.
- **Topics:** persistent + non-persistent; tenants/namespaces parsed from the `pulsar://...` URI; sharded-topic helpers (`PublishToShardedPulsarTopics`).
- **CloudEvents interop:** `UsePulsarWithCloudEvents(...)`.
- **Headers:** full envelope ↔ Pulsar property mapping via `PulsarEnvelopeMapper`.

### Gaps vs idiomatic Pulsar and vs Wolverine's Kafka transport

| Capability | Kafka | Pulsar | Child issue |
|---|---|---|---|
| Schema / typed messages | ✅ Avro + JSON Schema Registry | ❌ raw bytes only | PUL-8 |
| Negative acknowledgment | n/a | ❌ | PUL-2 |
| Reader / bounded replay | ✅ `ReplayKafkaTopicAsync` (#3147) | ❌ | PUL-9 |
| Ephemeral hot-tail / broadcast | ✅ `TailFromLatest` (#3146) | ❌ | PUL-9 |
| Cold-start position | ✅ `BeginAtEarliest/Latest` (#3146) | ❌ | PUL-3 |
| Ack/commit strategy choice | ✅ `CommitMode` ×4 (#3150) | ❌ per-message ack only | PUL-5 |
| Per-consumer/producer config hooks | ✅ `ConfigureConsumer/Producer` | ❌ global builder only | PUL-4 |
| Multi-topic / regex subscription | ✅ topic groups | ❌ single topic/endpoint | PUL-6 |
| Producer dedup / transactions | ⚠️ idempotent producer + read-committed (#3149) | ❌ | PUL-10 |
| Non-blocking tiered retry DSL | ✅ `MoveToKafkaRetryTopic` (#3148) | ⚠️ retry-letter topic, different model | PUL-7 |
| DLQ sender wiring | ✅ + `ExternallyOwned` (#3104) | ⚠️ stub TODO | PUL-1 |

### Cross-framework comparison

- **MassTransit:** no Pulsar transport; the "riders" architecture could host it but the team has stated no current plans.
- **NServiceBus / Brighter:** no Pulsar support.
- **Spring for Apache Pulsar (Java):** the reference integration — `@PulsarListener`/`@PulsarReader`, schema inference, nack + ack-timeout redelivery backoff, `DeadLetterPolicy`, batch consumption, pattern subscriptions, pause/resume. PUL-2/5/6/8/9 are the items that bring Wolverine toward this surface.

**Net:** Wolverine is effectively the only high-level .NET application framework with a real Pulsar transport, so this re-evaluation is about reaching idiomatic-Pulsar / Spring-Pulsar parity and matching the bar the Kafka transport just set — not about catching a .NET competitor.
