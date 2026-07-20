# Wolverine 6.17: The Community-Powered Messaging Release

[Wolverine 6.17](https://github.com/JasperFx/wolverine/releases/tag/V6.17.0) just went out the door, and it's a *big* one — 40+ pull requests, four first-time contributors, a brand new transport option, and a sweep that brought advanced multi-tenancy support to essentially every messaging transport Wolverine ships.

Why so big? Partially because I went on a three night vacation and the community decided that was the perfect moment to throw in issues and pull requests left and right. And honestly, that's the story I want to tell here.

Just yesterday I published [Things That Have Worked for Our OSS Community](https://jeremydmiller.com/2026/07/08/things-that-have-worked-for-our-oss-community/), about the practices that have made the Critter Stack easier to grow: reusable **compliance test suites**, **orthogonal code** that composes instead of duplicating, and **standardized test automation** that lets contributors (and yes, AI agents) follow consistent, proven patterns. Wolverine 6.17 is what those practices look like in release-note form. Nearly every headline item below either came from the community or was only feasible on this timeline *because* of that groundwork.

Let's take the tour.

---

## Community Contributions Front and Center

### 🆕 A native RavenDB control queue — from the community, hardened by compliance tests

[Daniel Winkler](https://github.com/danielwinkler) contributed a **native RavenDB-backed control queue** for Wolverine ([#3285](https://github.com/JasperFx/wolverine/pull/3285)). If you're using RavenDB for message persistence, Wolverine's internal node-to-node communication (leader election, agent assignment, health checks) now runs through RavenDB itself — no external broker and no database polling fallback required. It's registered automatically when you call `UseRavenDbPersistence()`.

Here's the part that speaks directly to the OSS-community post: after Daniel's PR landed, making the `ravendb://` transport truly first-class was mostly a matter of **bolting on the existing compliance suites** — the full `TransportCompliance` battery plus the leadership/control-queue compliance tests that every other control transport already passes ([#3294](https://github.com/JasperFx/wolverine/pull/3294)). A community contributor built the feature; the standardized test infrastructure told us exactly what "done and trustworthy" means. That's the multiplier effect in action.

### 🆕 NATS: dynamic subjects, JetStream dedup, and per-tenant connections

[thedonmon](https://github.com/thedonmon) delivered a substantial upgrade to the NATS transport ([#3283](https://github.com/JasperFx/wolverine/pull/3283)): **dynamically computed subjects** via an `ISubjectResolver` hook, **JetStream deduplication windows** (`WithDeduplicationWindow()`), and **per-tenant NATS connections**. This PR did double duty — it also seeded the transport-wide multi-tenancy sweep described below, because once one transport shows the pattern, the compliance tests make it cheap to demand parity everywhere.

### 🐛 Sharp-eyed fixes from returning and first-time contributors

- [lahma](https://github.com/lahma) fixed `CircuitWatcher.Dispose()` failing to stop the ping-until-reconnected loop ([#3326](https://github.com/JasperFx/wolverine/pull/3326)) *and* caught the per-topic Kafka builder methods dropping SASL_SSL credentials from `ConsumerConfig`/`ProducerConfig` ([#3344](https://github.com/JasperFx/wolverine/pull/3344)) — the kind of production-hardening fix that only comes from people running this stuff for real.
- [robertdusek](https://github.com/robertdusek) (first contribution!) fixed the outbox so that discarding after a rolled-back outbox commit properly clears the incoming envelope ([#3327](https://github.com/JasperFx/wolverine/pull/3327)).
- [Steve-XYZ](https://github.com/Steve-XYZ) (first contribution!) fixed the shared-memory transport to copy envelopes at the transport handoff so in-process test transports can't accidentally share mutable state ([#3333](https://github.com/JasperFx/wolverine/pull/3333)).
- [knotekbr](https://github.com/knotekbr) (first contribution!) contributed a whole new package: **WolverineFx.Http.AspVersioning**, integrating [Asp.Versioning.Http](https://github.com/dotnet/aspnet-api-versioning) with Wolverine.HTTP endpoints ([#3324](https://github.com/JasperFx/wolverine/pull/3324)).
- [meyc-v1](https://github.com/meyc-v1) (first contribution!) linked up the community-built **Salesforce Pub/Sub transport** from the Wolverine docs ([#3337](https://github.com/JasperFx/wolverine/pull/3337)) — a whole transport built *outside* the Wolverine repository, which is exactly what the orthogonal transport model is supposed to enable.

### 🤝 Community-initiated, finished in collaboration

Several more 6.17 features started as community pull requests that I finished up and merged with additional tests — credit where it's due:

- **Configuring `ServiceBusProcessorOptions` for Azure Service Bus listeners** was initiated by [jorik](https://github.com/jorik) ([#3286](https://github.com/JasperFx/wolverine/pull/3286) → [#3293](https://github.com/JasperFx/wolverine/pull/3293)). You can now tune the full processor options (prefetch, max concurrent calls, etc.) on any Azure Service Bus listening endpoint.
- **Explicit transactional `DbContext` selection for multi-`DbContext` handlers** was initiated by [KhaledZaabat](https://github.com/KhaledZaabat) ([#3284](https://github.com/JasperFx/wolverine/pull/3284) → [#3295](https://github.com/JasperFx/wolverine/pull/3295)) — when a handler touches more than one EF Core `DbContext`, you can now say which one owns the transaction and the Wolverine outbox.
- **Per-tenant agent fan-out with database-affine assignment** was initiated by [erdtsieck](https://github.com/erdtsieck) ([#3281](https://github.com/JasperFx/wolverine/pull/3281) → [#3328](https://github.com/JasperFx/wolverine/pull/3328)) — more on the event-subscription side of the house, but a big deal for folks running sharded, multi-tenanted Marten stores under Wolverine-managed projection distribution.

---

## 🚀 Named Brokers and Broker-per-Tenant, Everywhere

The biggest single theme of 6.17: **filling in the remaining gaps in "named broker" and "broker per tenant" support across every external messaging transport where it makes sense.** These capabilities used to be solid for Rabbit MQ and Azure Service Bus and hit-and-miss everywhere else. As of 6.17, the matrix is full:

| Transport | Named brokers | Broker per tenant | PR |
|-----------|:-:|:-:|----|
| Kafka | — | ✅ new | [#3315](https://github.com/JasperFx/wolverine/pull/3315) |
| AWS SQS | — | ✅ new | [#3316](https://github.com/JasperFx/wolverine/pull/3316) |
| AWS SNS | ✅ new | ✅ new | [#3317](https://github.com/JasperFx/wolverine/pull/3317) |
| GCP Pub/Sub | ✅ new | ✅ new | [#3318](https://github.com/JasperFx/wolverine/pull/3318) |
| MQTT | ✅ new | ✅ new | [#3319](https://github.com/JasperFx/wolverine/pull/3319) |
| Pulsar | ✅ new | ✅ new | [#3320](https://github.com/JasperFx/wolverine/pull/3320) |
| Redis | ✅ new | ✅ new | [#3321](https://github.com/JasperFx/wolverine/pull/3321) |
| NATS | ✅ new | ✅ (community, [#3283](https://github.com/JasperFx/wolverine/pull/3283)) | [#3314](https://github.com/JasperFx/wolverine/pull/3314) |

Quick refresher on what these mean:

**Named brokers** let one application talk to *multiple distinct brokers of the same type* — say, your team's Redis plus a legacy system's Redis:

```csharp
var analytics = new BrokerName("analytics");

builder.UseWolverine(opts =>
{
    // The "main" broker
    opts.UseRedisTransport("localhost:6379");

    // A completely separate, additional broker
    opts.AddNamedRedisBroker(analytics, "analytics-server:6379");

    opts.PublishMessage<PageViewed>()
        .ToRedisStreamOnNamedBroker(analytics, "pageviews");

    opts.ListenToRedisStreamOnNamedBroker(analytics, "clicks", "wolverine");
});
```

**Broker per tenant** is full physical tenant isolation: each tenant gets its *own cluster*, and Wolverine routes messages to the right broker based on the tenant id of the current message or operation — same topology, zero code changes in your handlers:

```csharp
builder.UseWolverine(opts =>
{
    opts.UseKafka("shared-cluster:9092");

    // Dedicated Kafka cluster per tenant
    opts.UseKafka("shared-cluster:9092")
        .AddTenant("acme", "acme-cluster:9092")
        .AddTenant("initech", "initech-cluster:9092");

    opts.PublishMessage<OrderPlaced>().ToKafkaTopic("orders");
});

// Publishing for a tenant just works — this lands on acme-cluster:9092
await bus.PublishAsync(new OrderPlaced(...), new DeliveryOptions { TenantId = "acme" });
```

Now, the "how did eight transports get this in one release?" question is exactly what [the OSS-community post](https://jeremydmiller.com/2026/07/08/things-that-have-worked-for-our-oss-community/) is about:

1. **Compliance tests** — every transport already passes the same reusable `TransportCompliance` suites, so "does the tenant-routed endpoint behave exactly like a normal endpoint?" is a question the test infrastructure answers mechanically, per transport.
2. **Orthogonal code** — multi-broker and multi-tenant routing are modeled *once* in Wolverine's core endpoint/routing model. Each transport only supplies the "give me a connection for this broker/tenant" piece; the routing, fallback-to-default semantics, and lifecycle management are shared.
3. **Standardized test automation** — the per-transport test projects follow the same harness recipes, so the pattern proven in the NATS community PR ([#3283](https://github.com/JasperFx/wolverine/pull/3283)) could be replicated across Kafka, SQS, SNS, Pub/Sub, MQTT, Pulsar, and Redis quickly and *safely*.

The [transport multi-tenancy issue sweep](https://github.com/JasperFx/wolverine/issues/3303) (#3303–#3310) went from filed to shipped in under two weeks. That's not heroics; that's infrastructure paying rent.

---

## 📦 Message Batching Grew Up

Wolverine's [message batching](https://wolverinefx.io/guide/handlers/batching.html) got a focused, multi-phase overhaul ([GH-3289](https://github.com/JasperFx/wolverine/issues/3289)) aimed at the two questions everyone eventually hits in production: *"can I de-duplicate within a batch?"* and *"what happens when one poison message fails the whole batch?"*

**De-duplication with `CoalesceBy`** ([#3300](https://github.com/JasperFx/wolverine/pull/3300)) — when a burst of messages for the same logical key arrives, the handler now only sees the last one per key, while every original message still settles (acks) with the batch:

```csharp
opts.BatchMessagesOf<RecalculateScores>(batching =>
{
    // 500 queued recalcs for the same aggregate → the handler sees 1
    batching.CoalesceBy((RecalculateScores x) => x.AggregateId);
});
```

**Poison-item isolation** — a family of tools for keeping one bad message from poisoning its whole batch, each fitting a different failure shape:

- If your handler can *name* the bad item, throw `ApplyItemException` and Wolverine dead-letters just that member while replaying or acking the rest ([#3302](https://github.com/JasperFx/wolverine/pull/3302)):

```csharp
public static void Handle(ImportRecord[] batch)
{
    var poison = batch.Where(x => !x.IsValid).ToArray();
    if (poison.Any())
    {
        throw ApplyItemException.DeadLetterAndReplayOthers(poison);
    }

    // process the batch...
}
```

- If a specific *exception type* means "one member is bad but I don't know which," the `IsolateBatchMembers()` error policy re-runs members individually so only the true culprit is dead-lettered ([#3311](https://github.com/JasperFx/wolverine/pull/3311)):

```csharp
opts.OnException<DataMappingException>().IsolateBatchMembers();
```

- And for fully *opaque* failures, `ProbeIndividuallyAfter(n)` kicks in after the whole batch has failed *n* times ([#3312](https://github.com/JasperFx/wolverine/pull/3312)):

```csharp
opts.BatchMessagesOf<ImportRecord>(batching =>
{
    batching.ProbeIndividuallyAfter(2);
});
```

Rounding it out: a startup diagnostic that warns (or asserts) when a direct `Handle(T)` handler silently shadows your batch handler ([#3301](https://github.com/JasperFx/wolverine/pull/3301)), `ApplyItemException` correctly poisoning *every* member behind a coalesced key ([#3313](https://github.com/JasperFx/wolverine/pull/3313)), and properly documented settlement/durability semantics ([#3299](https://github.com/JasperFx/wolverine/pull/3299)).

Notice the shape of the error-handling work: `IsolateBatchMembers()` is just another continuation in Wolverine's *composable* error-handling policy model — the same `OnException<T>()` grammar you already use for retries, requeues, and dead-lettering. That's the "orthogonal code" point from [the community post](https://jeremydmiller.com/2026/07/08/things-that-have-worked-for-our-oss-community/): because error handling is a policy pipeline rather than transport-specific spaghetti, a new batching-specific strategy slots in without touching any transport.

---

## 🐛 Messaging Reliability, Odds and Ends

A few more messaging items worth your attention in 6.17:

- **Buffered local queues no longer drop cascaded messages** under extreme load ([#3322](https://github.com/JasperFx/wolverine/pull/3322)) — the tail end of tracking down a silent-drop past 10K queued messages, fixed jointly with JasperFx core.
- **The dead letter queue table is now indexed for replay and cleanup scans** ([#3323](https://github.com/JasperFx/wolverine/pull/3323)). If you've ever accumulated a *large* `wolverine_dead_letters` table, the durability agent's replay and expiration polling no longer full-scans it.
- **Open Telemetry: the `InlineReceiver` no longer stomps the pipeline's `Error` activity status** ([#3292](https://github.com/JasperFx/wolverine/pull/3292)), so failed inline message processing shows up honestly in your traces.

## Beyond Messaging

Not messaging, but too good to skip: Wolverine.HTTP picked up support for the brand-new **HTTP `QUERY` verb** ([RFC 10008](https://www.rfc-editor.org/rfc/rfc10008), [#3296](https://github.com/JasperFx/wolverine/pull/3296)) — think "GET with a body" for complex query criteria — plus support for special characters in route templates ([#3297](https://github.com/JasperFx/wolverine/pull/3297)). And there was significant work on Wolverine-managed event subscription distribution for multi-tenanted Marten and Polecat stores that deserves its own post.

---

## The Takeaway

I'll say it one more time: go read [Things That Have Worked for Our OSS Community](https://jeremydmiller.com/2026/07/08/things-that-have-worked-for-our-oss-community/) with this release in mind. A community member shipped a whole control transport, and the compliance suites certified it. Another community member shipped advanced NATS multi-tenancy, and the orthogonal transport model let us propagate that capability across seven more transports in days. Four people made their first contribution to Wolverine in a single release — and one of them was documentation pointing at a transport the community built entirely outside our repository.

Compliance tests, orthogonal code, and standardized test automation aren't glamorous. But they're why a three-night vacation produced the biggest Wolverine release in months instead of a merge-conflict pile.

As always: upgrade, kick the tires, and [tell us what breaks](https://github.com/JasperFx/wolverine/issues). Clearly, we listen.
