# AI/LLM Integration Research — Prior Art for a Wolverine/Marten Feature Set

*Research date: 2026-07-12. Sources: deep-research workflow (25 sources fetched, 115 claims extracted, 25 top claims adversarially verified 3-0) plus a dedicated AxonIQ/KurrentDB sweep. Seed question: prior art for AI-gateway-style resilience/observability around LLM calls in messaging frameworks and event stores, per https://dotnetdigest.com/building-an-ai-gateway-in-net.*

## Executive summary

1. **The .NET messaging field is empty.** MassTransit v9 is purely the commercial/Massient licensing transition — zero AI features, no MassTransit.AI, no IChatClient integration (verified against public announcements as of July 2026). Nothing from NServiceBus/Rebus/Brighter surfaced either. A Wolverine AI integration would be a first mover.
2. **`IChatClient` is the sanctioned seam and Microsoft prescribes the packaging.** A `Wolverine.AI` package should reference only `Microsoft.Extensions.AI.Abstractions`, derive middleware from `DelegatingChatClient`, and expose `Use*` extensions on `ChatClientBuilder`. Caching (exact-history) and OTel GenAI-semconv telemetry are solved in-box; **routing/fallback, budget enforcement, and durable/transactional resilience are not** — that's the gap.
3. **Nobody shipped the durable LLM-callout primitive.** AxonIQ and Kurrent both pivoted their positioning hard to AI (Oct 2025 / Dec 2024) but answer "LLM call from an event handler" with generic at-least-once + retry + park/DLQ + an idempotency warning in the docs. First-class support — a memoized "AI side effect" that records the LLM response as an event so retries and replays reuse it instead of re-billing — has no prior art anywhere.
4. **The event-sourcing + AI angle is genuine white space.** The main workflow's verification pass produced *zero* surviving claims for LLM-from-projections/subscriptions prior art. The one hard principle: projections must be deterministic/replayable, which rules out LLM calls inside projection logic and points at side-effect subscriptions as the right home.

## What Microsoft.Extensions.AI already gives us (don't rebuild)

- `IChatClient` + `IEmbeddingGenerator<TInput,TEmbedding>` in `Microsoft.Extensions.AI.Abstractions`; framework libraries reference abstractions only, apps reference the full package. ([learn.microsoft.com/dotnet/ai/microsoft-extensions-ai](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai))
- `ChatClientBuilder` composes `DelegatingChatClient` decorators; first `Use*` registered = outermost. Built-ins: `UseDistributedCache`, `UseFunctionInvocation`, `UseOpenTelemetry`, `UseLogging`, `ConfigureOptions`. Microsoft explicitly frames these as "a small subset" and invites third-party middleware; the canonical worked example is a rate limiter on `System.Threading.RateLimiting`. ([learn.microsoft.com/dotnet/ai/ichatclient](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient))
- `DistributedCachingChatClient` = exact-match caching keyed on chat history + `ChatOptions` (no semantic caching; `RawRepresentation` not cached). `OpenTelemetryChatClient` emits OTel GenAI semantic conventions (still experimental; semconv v1.37).
- **Absent from built-ins:** cross-provider routing/fallback, cost/budget enforcement, durability. These are the framework-integration opportunities.
- API drift warning: preview-era `.Use(inner).AsChatClient` became `AsIChatClient` at GA — re-check current package surface before implementing.

## Richest framework prior art: Spring AI 2.0 (GA June 2026)

- **Advisors API** = around-style interceptor chain: `ChatClientResponse adviseCall(ChatClientRequest, CallAdvisorChain)` / `Flux<ChatClientResponse> adviseStream(...)`. The abstraction is **split into sync and streaming interfaces** with guidance to implement both — any IChatClient wrapper faces the same fork. ([docs.spring.io/spring-ai/reference/api/advisors.html](https://docs.spring.io/spring-ai/reference/api/advisors.html))
- Batteries-included advisor catalog: chat memory (message + vector-store variants), RAG (`QuestionAnswerAdvisor`, `RetrievalAugmentationAdvisor`), content safety (`SafeGuardAdvisor`), auto-registered `ToolCallingAdvisor`.
- **Spring 2.0 lifted the tool-calling loop into the middleware chain** as a recursive advisor that re-enters the downstream chain until the model stops requesting tools. Ordering relative to the loop is semantically meaningful: a memory advisor *outside* the loop persists only final user/assistant messages; *inside* the loop it persists the full tool transcript, and the framework auto-disables internal history to avoid duplicate writes. This is the state-of-the-art answer to "where does the agentic loop live in a pipeline." ([spring.io blog: composable tool calling](https://spring.io/blog/2026/06/15/spring-ai-composable-tool-calling/))

## Durable execution prior art

- **Microsoft Agent Framework durable workflows** (May 2026): automatic checkpointing on the Durable Task stack, per-executor granularity (each executor = a durable activity, `dafx-` prefixed) so completed LLM steps don't re-run after a crash. Criticisms (Diagrid): manual resume, no tool-level RetryPolicy, Azure dependence; the framework's *native* in-process CheckpointManager is only superstep-granular. ([devblogs: durable workflows in MAF](https://devblogs.microsoft.com/dotnet/durable-workflows-in-microsoft-agent-framework/))
- Temporal/Restate converge on "LLM call = retryable activity inside deterministic orchestration."
- **Differentiation axis:** Wolverine's durable inbox/outbox + scheduled retries can offer per-step durability broker- and cloud-agnostically. Nobody checkpoints at sub-call granularity (per tool invocation inside an agentic loop) yet.

## AI-gateway feature checklist (Cloudflare / LiteLLM / Portkey / Kong)

- Canonical set: caching, rate limiting, automatic retry + **model/provider-granularity ordered fallback** (configurable backoff; telemetry reports which fallback step served the request), token/cost analytics, request logging. ([developers.cloudflare.com/ai-gateway](https://developers.cloudflare.com/ai-gateway/))
- **Cost is a first-class telemetry dimension** — requests, tokens, dollars. Kong is dinged competitively for weak cost intelligence; LiteLLM does per-key/team budget caps. Any Wolverine observability story needs token/cost accounting alongside OTel spans.
- Fallback-at-model-granularity maps naturally onto Wolverine error policies; budget caps map onto per-endpoint/per-tenant/per-message-type policies (open question: budget-exceeded → backpressure/queue semantics has no prior art).

## AxonIQ (deep-dive)

- **Oct 30, 2025:** "AxonIQ Platform" launch — AF5 + Axon Server 2025.2 + "Agents"; pitch = architecture-level explainability ("persistent event-based memory," time-travel debugging, MCP extensions in Axon Server). **AI lives in the commercial platform; Axon Framework 5.x itself has no LLM API, no Spring AI integration.** (AF 5.0 GA Oct 2025; 5.2.0 Jul 9, 2026.)
- **Architectural stance worth quoting:** agents enter *through the command/query bus*, validated by normal business logic — never appending events directly. Their MCP demo drives `RegisterBike` commands via an OpenAI agent "without bypassing safeguards."
- **AxonIQ Insights** (most concrete shipped AI feature): subscribes to event streams, batches to Parquet in a DuckDB "EventLake" sidecar; SQL over Postgres wire protocol; NL chat where the LLM sees the schema and generates SQL; embedded MCP server. **AI reads a derived analytical replica, never the live store.**
- **Side-effect machinery (closest analog to Wolverine + Marten subscriptions):**
  - Streaming processors retry with incremental backoff 1s→60s, stalling the segment.
  - **Sequence-aware DLQ**: `SequencedDeadLetterQueue` parks the failed event *and all subsequent events with the same sequence id*, so projections never build on inconsistent state. (Dropped in AF 5.0, reintroduced 5.1.0.)
  - Docs explicitly warn: enabling DLQ ⇒ at-least-once ⇒ handlers must be idempotent; token-steal re-fires side effects (emails, sagas, queue messages named).
  - **Replay-awareness primitives — the most reusable idea:** `resetTokens()` triggers replay; handlers opt out of side effects during replay via `@DisallowReplay` / `@AllowReplay`, a `ReplayStatus` parameter (REGULAR vs REPLAY), and `@ReplayContext` injection. Built generically for emails years before LLMs; nothing LLM-specific shipped on top.
- **No embeddings/vector/RAG** anywhere in Axon Server or the framework.
- Unverified: Developer Agent 2.0 capabilities, multi-agent orchestration details, Axon Server "MCP extensions" specifics (press-release wording only).

## Kurrent / KurrentDB (deep-dive)

- **Dec 2024:** Event Store → Kurrent rebrand + $12M; "the event-native data platform"; homepage claims "if you're building agentic AI, Kurrent is the only database that actually understands history" — but names no concrete AI features.
- **KurrentDB MCP Server** (May 2025, flagship AI deliverable): Python/MIT/stdio; 8 tools — `read_stream`, `list_streams`, `build_projection`, `create_projection`, `update_projection`, `test_projection`, `write_events_to_stream`, `get_projections_status`. Signature feature: **self-correcting projection authoring** (agent writes JS projection, runs `test_projection`, iterates on faults). Dev-time positioning; modest repo activity. ([github.com/kurrent-io/mcp-server](https://github.com/kurrent-io/mcp-server))
- **KurrentDB 26.0** (GA Jan 2026), marketed "Non-disruptive Integration for AI Systems," actually shipped: Kafka source connector, **Relational Sink** (declarative reducers → auto-maintained Postgres/SQL Server read models), user-defined secondary indexes. **No vector search, no embedding generation, no MCP in the DB** — "for AI systems" means "your events reach AI/analytics systems without custom code."
- **Callout machinery:** persistent subscriptions = ack/nack (retry/skip/**park**); park after `maxRetryCount` to `$persistentsubscription-{stream}::{group}-parked`; parked replayable with `stopAt`; checkpoint streams; documented at-least-once. **Documented pitfall: ordering is not guaranteed with persistent subscriptions**, and parking is *not* sequence-aware (a parked event's successors keep flowing) — the sequence-consistency gap Axon solved and Kurrent didn't. Connectors run in-server, at-least-once, shared retry/backoff resilience config; HTTP sink POSTs events individually (no batching).
- **Samples, not product:** event-driven multi-agent coordination via persistent subscriptions (Sept 2025, no idempotency/cost-on-replay guidance); LangGraph checkpointer persisting agent state as KurrentDB events with export-run-as-OTel-trace ("not production-ready").
- **Capacitor** (June 2026, private preview): "shared memory for coding agents" — records agent sessions as immutable events, MCP-queryable; Kurrent dogfooding event-store-as-agent-memory as a standalone dev tool.

## Cross-cutting takeaways → Wolverine/Marten opportunity map

1. **Durable LLM-callout primitive (no prior art anywhere).** A memoized "AI side effect": Wolverine handler/subscription makes the IChatClient call once, records the response durably (as an event or inbox-adjacent record), and retries/replays reuse the recorded response instead of re-billing. Combines outbox, idempotency, and cost control in one feature.
2. **Replay-awareness surfaced to handler code.** Axon's `@DisallowReplay`/`ReplayStatus` is the proven shape; the Marten/Polecat equivalent is an `IsReplay`/rebuild flag on subscription context + a policy that suppresses or memoizes callouts during projection rebuilds. Marten subscriptions (at-least-once, checkpointed, retry-capable) are the right home — never inline projections (determinism).
3. **Wolverine handlers as the AI-gateway pipeline.** A `Wolverine.AI` package (abstractions-only dependency): error policies → model/provider-granularity fallback; scheduled retries → durable retry of model calls; outbox → transactional AI side effects; OTel + token/cost accounting as first-class telemetry; budget policies per endpoint/tenant/message-type. MassTransit's absence makes this a first-mover play.
4. **MCP-over-the-event-store is table stakes.** Kurrent's 8-tool server and AxonIQ's Insights both shipped it; both keep agents off the raw store (dev-time scoping / derived replica / commands-only). A Marten MCP server (read streams, author + self-test C# projections, write test events) matches the flagship deliverable of both vendors — with a stronger self-test loop than Kurrent's JS projections. Adopt Axon's policy verbatim: agents enter via Wolverine handlers/HTTP endpoints, never append events directly.
5. **"Event store as agent memory" is the shared marketing hill; implementations are thin.** Marten + pgvector could make it concrete (embedding-per-event via `IEmbeddingGenerator` from a subscription, semantic retrieval over history) in a way neither vendor shipped. The LangGraph-checkpointer + rebuild-run-as-OTel-trace sample is a cheap, high-visibility .NET equivalent (Marten-backed agent-state store).
6. **Keep OSS core AI-free; ship AI surface in add-on packages / CritterWatch-style tooling** — the monetization pattern both vendors follow.

## Open questions (no prior art found)

- Streaming responses (`GetStreamingResponseAsync`) vs durable outbox: semantics for a partially-consumed stream. Spring's dual-interface split frames the problem; nobody has durability answers.
- Budget-exceeded → backpressure: mapping cost caps onto queue/rate-limiting semantics.
- Sub-call checkpoint granularity (per tool invocation inside an agentic loop).
- Sequence-aware parking + expensive callouts: combining Axon-style sequence DLQ semantics with memoized AI side effects in Marten subscriptions.

## Coverage caveats

Angles that produced no *verified* claims in the main workflow (unverified ≠ absent): the dotnetdigest seed post itself, NServiceBus/Rebus/Brighter, Dapr Conversation API/dapr-agents, Kafka/Flink inference detail (Confluent's Flink SQL `ML_PREDICT` LATERAL-join pattern appeared in extraction but wasn't in the verified top-25), Akka.NET agentic patterns, Temporal/Restate beyond blog level, LangGraph/SK process framework. OTel GenAI semconv is experimental. MassTransit finding is an absence claim (public announcements/search, not commercial source audit). Spring AI 2.0 and MAF durable workflows are ≤2 months old — re-verify API surfaces before implementing.
