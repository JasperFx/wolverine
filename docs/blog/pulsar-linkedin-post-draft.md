<!--
DRAFT LinkedIn post — Wolverine Pulsar transport overhaul.
STATUS (verified 2026-06-23): all epic #3176 features (#3177-#3186 + Avro #3213)
are MERGED TO main, version 6.14.0 — NOT yet released (latest tag V6.13.1).
Honest framing = "just landed / shipping in 6.14.0", not "available on NuGet now".
Code samples below are lifted from the real shipped API + tests. Placeholder voice —
interview Jeremy to finalize wording, links, hashtags.
-->

# LinkedIn post — draft (v2, with samples)

---

A few weeks ago we gave Wolverine's **Kafka** transport a top-to-bottom overhaul. This week we did the same for **Apache Pulsar** — and it just landed in `main` for the upcoming 6.14.0 release. 🛰️

First, the part people don't always realize: in the .NET world, Wolverine is essentially the *only* high-level application framework with a real, first-class Pulsar transport. MassTransit and NServiceBus don't have one.

And the baseline was already solid — all four subscription types, native retry-letter **and** dead-letter topics, scheduled delivery, persistent/non-persistent topics, multi-tenancy, CloudEvents interop, and everything that makes Wolverine *Wolverine*: the mediator + message bus, durable inbox/outbox, middleware, and uniform error-handling policies.

But "solid" isn't "idiomatic." Pulsar has a personality of its own, and this release leans into it. A tour, with code:

**🔹 Strongly-typed schemas (JSON + Avro), with broker-side enforcement** — no more raw bytes:
```csharp
opts.PublishMessage<Order>().ToPulsarTopic("orders").UseJsonSchema<Order>();
opts.ListenToPulsarTopic("orders").UseAvroSchema<Order>();
```

**🔹 Bounded, one-shot replay** through your normal handlers — without disturbing the live subscription's cursor:
```csharp
await host.ReplayPulsarTopicAsync(new PulsarReplayRequest { Topic = "orders" });
```

**🔹 Multi-topic & regex subscriptions** — one consumer over many topics:
```csharp
opts.ListenToPulsarTopic("orders")
    .TopicsPattern(new Regex("orders-.*"), RegexSubscriptionMode.All);
```

**🔹 Non-blocking tiered retry topics** — the Spring/Uber pattern, as a first-class error policy:
```csharp
opts.OnException<TransientFailure>()
    .MoveToPulsarRetryTopic(2.Seconds(), 10.Seconds(), 1.Minutes());
```

**🔹 Acknowledgment strategies + a "hot tail" broadcast mode**:
```csharp
opts.ListenToPulsarTopic("events").AcknowledgeInBatches(50, 2.Seconds());
opts.ListenToPulsarTopic("notifications").TailFromLatest(); // every node sees every message
```

Plus: subscription start positions (`BeginAtEarliest`/`BeginAtLatest`), per-consumer/producer tuning hooks, native per-message redelivery, and producer-side deduplication.

One thing I appreciated while building this: we verified every feature against the actual DotPulsar client API *before* committing to the plan — which surfaced a couple of honest constraints (the .NET client has no native negative-ack and no transactions API yet) and let us right-size the work instead of overpromising. Engineering in the open, constraints included.

If you're running Pulsar on .NET — or thinking about it — I'd love your feedback once 6.14.0 drops.

👉 [link to epic #3176]
👉 [link to Wolverine Pulsar docs]

#dotnet #ApachePulsar #eventdriven #messaging #opensource #distributedsystems

---

## Notes for Jeremy (remove before posting)

- **Status is honest:** everything shown is **merged to `main`**, shipping in **6.14.0** (not yet released — latest tag is V6.13.1). The post says "just landed / once 6.14.0 drops" rather than implying it's on NuGet today. If 6.14.0 is published before you post, change to present tense.
- **All samples are real**, lifted from the shipped API + `Wolverine.Pulsar.Tests` (e.g. `UseJsonSchema<T>()`/`UseAvroSchema<T>()`, `ReplayPulsarTopicAsync`, `MoveToPulsarRetryTopic`, `AcknowledgeInBatches`, `TailFromLatest`, `TopicsPattern`). Double-check `RegexSubscriptionMode.All` is the member name you want to showcase.
- **Length:** ~320 words + 5 short snippets — long for LinkedIn but fine for a dev audience; a carousel/screenshots of the snippets often performs better than inline code. A ~120-word punchy variant is easy to cut.
- **Voice:** first person singular. Adjust to your usual posting voice.
- **Two links to fill:** epic https://github.com/JasperFx/wolverine/issues/3176 and the Pulsar docs page.
- **Optional:** tie visually to the Kafka "grew up" post so the two read as a series.
