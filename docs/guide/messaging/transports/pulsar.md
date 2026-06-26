# Using Pulsar <Badge type="tip" text="3.0" />

::: info
Fun fact, the Pulsar transport was actually the very first messaging broker to be supported
by Jasper/Wolverine, but for whatever reason, wasn't officially released until Wolverine 3.0. 
:::

## Installing

To use [Apache Pulsar](https://pulsar.apache.org/) as a messaging transport with Wolverine, first install the `WolverineFx.Pulsar` library via nuget to your project. Behind the scenes, this package uses the [DotPulsar client library](https://pulsar.apache.org/docs/next/client-libraries-dotnet/) managed library for accessing Pulsar brokers.

```bash
dotnet add package WolverineFx.Pulsar
```

To connect to Pulsar and configure senders and listeners, use this syntax:

<!-- snippet: sample_configuring_pulsar -->
<a id='snippet-sample_configuring_pulsar'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UsePulsar(c =>
    {
        var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
        c.ServiceUrl(pulsarUri!);
        
        // Any other configuration you want to apply to your
        // Pulsar client
    });

    // Publish messages to a particular Pulsar topic
    opts.PublishMessage<Message1>()
        .ToPulsarTopic("persistent://public/default/one")
        
        // And all the normal Wolverine options...
        .SendInline();

    // Listen for incoming messages from a Pulsar topic
    opts.ListenToPulsarTopic("persistent://public/default/two")
        .SubscriptionName("two")
        .SubscriptionType(SubscriptionType.Exclusive)
        
        // And all the normal Wolverine options...
        .Sequential();

    // Listen for incoming messages from a Pulsar topic with a shared subscription and using RETRY and DLQ queues
    opts.ListenToPulsarTopic("persistent://public/default/three")
        .WithSharedSubscriptionType()
        .DeadLetterQueueing(new DeadLetterTopic(DeadLetterTopicMode.Native))
        .RetryLetterQueueing(new RetryLetterTopic([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)]))
        .Sequential();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Pulsar/Wolverine.Pulsar.Tests/DocumentationSamples.cs#L12-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_pulsar' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The topic name format is set by Pulsar itself, and you can learn more about its format in [Pulsar Topics](https://pulsar.apache.org/docs/next/concepts-messaging/#topics). 

::: info
Depending on demand, the Pulsar transport will be enhanced to support conventional routing topologies and more advanced
topic routing later.
::: 

## Subscription Initial Position

When a Wolverine listener creates a **brand-new** Pulsar subscription, you can control where that subscription
starts reading. This is the Pulsar analogue of the Kafka transport's `BeginAtEarliest()` / `BeginAtLatest()`:

```csharp
opts.ListenToPulsarTopic("persistent://public/default/orders")
    // Replay from the earliest message still retained on the topic
    .BeginAtEarliest();

opts.ListenToPulsarTopic("persistent://public/default/notifications")
    // Only consume messages published after the subscription is created (the default)
    .BeginAtLatest();

// Or set it explicitly with the DotPulsar enum:
opts.ListenToPulsarTopic("persistent://public/default/audit")
    .SubscriptionInitialPosition(SubscriptionInitialPosition.Earliest);
```

This only affects the **first** read of a not-yet-existing subscription. Once the subscription exists, Pulsar
resumes from its committed cursor on every restart regardless of this setting, and `Earliest` can only replay
messages that are still retained on the topic. The default is `SubscriptionInitialPosition.Latest`, matching
DotPulsar's own default.

### Hot-tail / broadcast consume <Badge type="tip" text="6.8" />

Sometimes you want **every node** to see **every message** as it arrives — live dashboards, cache
invalidation, fan-out-to-all-instances — rather than the competing-consumer model where each message goes to
exactly one node on a shared subscription. Use `TailFromLatest()`:

```csharp
opts.ListenToPulsarTopic("persistent://public/default/live-events").TailFromLatest();
```

Each process consumes through its own **ephemeral, non-durable [Reader](https://pulsar.apache.org/docs/concepts-clients/#reader-interface)**
cursor starting at the tail, so every node receives all messages, never replays old data, and commits no
subscription cursor. This is the idiomatic Pulsar pattern for broadcast — the analogue of the Kafka
transport's `TailFromLatest()`.

A few things to know:

- Because the reader starts at the tail, only messages published **after** a node has attached its reader are
  delivered — there is no backlog replay.
- The reader cursor is throwaway and unacknowledged, so dead-letter / retry-letter queueing, native
  redelivery, and acknowledgment strategies do not apply to a hot-tail listener.
- Reach for `TailFromLatest()` when you want **all** nodes to process each message; use a normal `Shared` /
  `KeyShared` subscription when you want each message processed **once** across the cluster.

## Replaying a Topic <Badge type="tip" text="6.8" />

When you need to **reprocess** a window of a topic's history — error recovery, rebuilding downstream state,
replaying after a bug fix — Wolverine offers a **bounded, one-shot replay** that reads a range of a topic back
through the **normal handler pipeline**. It uses a throwaway, non-durable Pulsar `Reader` cursor and **never
touches any live durable subscription**, so steady-state consumption is completely untouched.

```csharp
// Programmatic API on IHost
await host.ReplayPulsarTopicAsync(new PulsarReplayRequest
{
    Topic = "persistent://public/default/orders",
    FromTimestamp = DateTimeOffset.UtcNow.AddHours(-1),  // or FromMessageId = someMessageId
    // ToTimestamp / ToMessageId optional — defaults to "now" (the topic's last message at start)
});
```

Start defaults to the earliest retained message and end defaults to the topic's last message at the moment the
replay begins, so omitting the bounds replays the whole topic as it stands and never tails live traffic
published after the replay started. Both `From`/`To` accept either a publish-time `DateTimeOffset` or a Pulsar
`MessageId` (the two are mutually exclusive on each end).

::: warning Replayed messages are re-handled
Each replayed message flows through your handlers again, exactly like live consumption. Handlers should be
**idempotent** (the same expectation as any at-least-once reprocessing). If you use the durable inbox, replayed
envelopes pass through the same inbox + de-duplication path.
:::

Replay reads forward to the end boundary and stops cleanly. It is a discrete operation that runs on its own
non-durable reader cursor, so it can safely run on a node that is also listening to the same topic on a durable
subscription.

## Multi-Topic & Pattern Subscriptions

A single Pulsar listener can consume from more than one topic, or from every topic matching a
regular expression — the Pulsar analogue of Kafka topic groups. This reduces the number of
consumers you need and lets a listener automatically pick up new topics that match a pattern.

```csharp
// One listener over several explicit topics
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .Topics(
        "persistent://public/default/orders-priority",
        "persistent://public/default/orders-bulk");

// One listener over every topic matching a regex pattern
opts.ListenToPulsarTopic("persistent://public/default/events-all")
    .TopicsPattern(new Regex("persistent://public/default/events-.*"));

// Pattern, restricted to non-persistent topics (default is persistent only)
opts.ListenToPulsarTopic("non-persistent://public/default/telemetry-all")
    .TopicsPattern(new Regex("non-persistent://public/default/telemetry-.*"),
        RegexSubscriptionMode.NonPersistent);
```

When a pattern is configured it takes precedence, and the topic the listener was created with is
used only as the Wolverine endpoint identity. Pattern subscriptions match topics that exist at
subscription time and pick up newly created matching topics as Pulsar discovers them.

## Per-Message Redelivery

By default, when a message fails and is requeued, the Pulsar listener acknowledges it and
re-publishes a fresh copy to the source topic. You can instead opt into Pulsar's native
per-message redelivery, where the message is left **unacknowledged** and Pulsar redelivers just
that one message (preserving its redelivery count) rather than creating a duplicate:

```csharp
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .UseNativeRedelivery();

// Combine with a retry policy that requeues on failure:
opts.Policies.OnException<TransientException>().Requeue(3);
```

For delayed / backoff redelivery (growing the delay between attempts), use the Pulsar
retry-letter topics instead — DotPulsar's client does not expose negative-acknowledgment backoff
or ack-timeout settings.

## Tiered Retry-Letter Policy <Badge type="tip" text="6.8" />

`RetryLetterQueueing(...)` configures Pulsar's native retry-letter topic per endpoint. For a
first-class, discoverable **error policy** — the Pulsar analogue of the Kafka transport's
`MoveToKafkaRetryTopic` — use `MoveToPulsarRetryTopic(...)`:

```csharp
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .SubscriptionType(SubscriptionType.Shared);

// On failure: redeliver after 5s, then 30s, then 2m, then dead-letter.
opts.OnException<TransientException>()
    .MoveToPulsarRetryTopic(5.Seconds(), 30.Seconds(), 2.Minutes());
```

Each `TimeSpan` is one retry tier. On the first failure the message is routed to the retry-letter
topic and redelivered after the first delay; on the next failure after the second delay; and so on.
After the last tier is exhausted the message lands in the dead-letter topic. The delays are wired
onto every Pulsar listener at startup (provisioning the retry-letter producer/consumer and the DLQ),
so you don't also need an explicit `RetryLetterQueueing(...)` call.

::: warning Requires a Shared or Key_Shared subscription
Pulsar message delaying only works on `Shared` / `KeyShared` subscriptions. Applying
`MoveToPulsarRetryTopic` to an `Exclusive` / `Failover` listener emits a startup warning and the
policy falls back to an inline retry. The policy is also Pulsar-only: a failure arriving over any
other transport falls back to an inline retry (and a startup warning is emitted when non-Pulsar
listeners are present).
:::

### By-key concurrency with Key_Shared

Pulsar's `Key_Shared` subscription is the idiomatic, **zero-extra-code** path for intra-partition
concurrency by key — the free Pulsar analogue of the Kafka transport's sticky by-key processing. With
a `Key_Shared` subscription, Pulsar distributes messages across the connected consumers (nodes) by
message key, so messages that share a key are always delivered to the **same** consumer in order,
while different keys are processed concurrently across the cluster:

```csharp
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .SubscriptionType(SubscriptionType.KeyShared);
```

Set the key on the way out via the ordinary Wolverine `GroupId` / partition-key conventions (the
producer stamps it as the Pulsar message key). This pairs naturally with `MoveToPulsarRetryTopic`,
which also requires `Shared` / `Key_Shared`.

## Customizing Consumers & Producers

Beyond the global `IPulsarClientBuilder` passed to `UsePulsar(...)`, you can customize the
individual DotPulsar consumer or producer per endpoint immediately before it is created — to set a
consumer/producer name, compression, batching, receive-queue size, routing mode, priority level,
and so on:

```csharp
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .ConfigureConsumer(consumer => consumer
        .ConsumerName("orders-worker")
        .PriorityLevel(1));

opts.PublishMessage<OrderPlaced>()
    .ToPulsarTopic("persistent://public/default/orders")
    .ConfigureProducer(producer => producer
        .ProducerName("orders-publisher")
        .CompressionType(CompressionType.Lz4));
```

The callback receives the same `IConsumerBuilder` / `IProducerBuilder` Wolverine uses internally, so
anything DotPulsar exposes is available. A listener also exposes `ConfigureProducer(...)` for the
producer it uses on the requeue/redelivery path.

## Acknowledgment Strategy

By default the listener acknowledges each message individually as it completes. On high-volume
subscriptions you can reduce broker chatter by acknowledging cumulatively or in batches:

```csharp
// Individual (default)
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .AcknowledgeIndividually();

// Cumulative — one ack confirms everything up to a point. Exclusive/Failover only.
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .AcknowledgeCumulative();

// Batched — individual acks flushed by count or interval
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .AcknowledgeInBatches(batchSize: 100, interval: TimeSpan.FromSeconds(1));
```

**Cumulative ack is only valid for Exclusive / Failover subscriptions** — configuring it on a
Shared / Key_Shared subscription throws a clear error at startup. Because Wolverine's buffered
listener can complete messages out of order, cumulative ack only ever advances to the highest
**contiguous-completed** message: it will never acknowledge a message that is still being processed,
so no in-flight work is lost. Batched ack has no such ordering constraint and is safe for any
subscription type.

## Read Only Subscriptions <Badge type="tip" text="3.13" />

As part of Wolverine's "Requeue" error handling action, the Pulsar transport tries to quietly create a matching sender
for each Pulsar topic it's listening to. Great, but that will blow up if your application only has receive-only permissions
to Pulsar. In this case, you probably want to disable Pulsar requeue actions altogether with this setting:

<!-- snippet: sample_disable_requeue_for_pulsar -->
<a id='snippet-sample_disable_requeue_for_pulsar'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UsePulsar(c =>
    {
        var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
        c.ServiceUrl(pulsarUri!);
    });

    // Listen for incoming messages from a Pulsar topic
    opts.ListenToPulsarTopic("persistent://public/default/two")
        .SubscriptionName("two")
        .SubscriptionType(SubscriptionType.Exclusive)
        
        // Disable the requeue for this topic
        .DisableRequeue()
        
        // And all the normal Wolverine options...
        .Sequential();

    // Disable requeue for all Pulsar endpoints
    opts.DisablePulsarRequeue();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Pulsar/Wolverine.Pulsar.Tests/DocumentationSamples.cs#L54-L79' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_requeue_for_pulsar' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you have an application that has receive only access to a subscription but not permissions to publish to Pulsar,
you cannot use the Wolverine "Requeue" error handling policy.

### Subscription behavior when closing connection

By default, the Pulsar transport will automatically close the subscription when the endpoints is being stopped.
If the subscription is created for you, and should be kept after application shut down, you can change this behavior.

<!-- snippet: sample_pulsar_unsubscribe_on_close -->
<a id='snippet-sample_pulsar_unsubscribe_on_close'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.UsePulsar(c =>
    {
        var pulsarUri = builder.Configuration.GetValue<Uri>("pulsar");
        c.ServiceUrl(pulsarUri!);
    });

    // Disable unsubscribe on close for all Pulsar endpoints
    opts.UnsubscribePulsarOnClose(PulsarUnsubscribeOnClose.Disabled);
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Pulsar/Wolverine.Pulsar.Tests/DocumentationSamples.cs#L84-L98' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pulsar_unsubscribe_on_close' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Global Partitioning

Pulsar topics can be used as the external transport for [global partitioned messaging](/guide/messaging/partitioning#global-partitioning). This creates a set of sharded Pulsar topics with companion local queues for sequential processing across a multi-node cluster.

Use `UseShardedPulsarTopics()` within a `GlobalPartitioned()` configuration:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePulsar();

        opts.MessagePartitioning.ByMessage<IMyMessage>(x => x.GroupId);

        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            // Creates 4 sharded Pulsar topics named "orders1" through "orders4"
            // with matching companion local queues for sequential processing
            topology.UseShardedPulsarTopics("orders", 4);
            topology.MessagesImplementing<IMyMessage>();
        });
    }).StartAsync();
```

This creates Pulsar topics named `orders1` through `orders4` with companion local queues `global-orders1` through `global-orders4`. Messages are routed to the correct shard based on their group id, and Wolverine handles the coordination between nodes automatically.

## Schema Support <Badge type="tip" text="6.8" />

Schema is one of Pulsar's defining features: the broker stores a schema per topic and enforces schema
**compatibility / evolution** rules when producers and consumers connect. Wolverine can register a **JSON
schema** for a topic so you get that broker-side registration and compatibility checking, while Wolverine
continues to own the message body (its normal `System.Text.Json` serialization):

```csharp
// Producing endpoint
opts.PublishMessage<OrderPlaced>()
    .ToPulsarTopic("persistent://public/default/orders")
    .UseJsonSchema<OrderPlaced>();

// Listening endpoint — register a compatible schema
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .UseJsonSchema<OrderPlaced>();
```

`UseJsonSchema<T>()` generates the Avro-format JSON schema Pulsar uses for `SchemaType.Json` from the CLR
type's public properties and registers it with the broker when the producer/consumer connects. The schema
covers the common POCO shapes (primitives, strings, enums and `Guid`/`DateTime` as strings, nullable value
types as `["null", T]` unions, arrays, and nested records) and falls back to `string` for anything it
can't map, so registration never fails on an exotic property.

::: info The body stays Wolverine-serialized
The schema is a **pass-through** over the bytes Wolverine already serializes — it declares the schema the
broker stores and checks for compatibility, but does not change how the body is encoded. Existing
raw-bytes and CloudEvents endpoints are unaffected (no schema is registered unless you opt in).
:::

For full control — a custom schema definition, a different `SchemaType`, or your own codec — supply an
`ISchema<ReadOnlySequence<byte>>` directly:

```csharp
opts.ListenToPulsarTopic("persistent://public/default/orders")
    .UsePulsarSchema(myCustomSchema);
```

### Avro

For genuine **Avro** on the wire, use `UseAvroSchema<T>()`. Unlike the JSON pass-through, the body is
Avro-encoded by DotPulsar's built-in `Schema.AvroISpecificRecord<T>()` and the broker registers the Avro
schema; Wolverine carries its metadata in the Pulsar message properties and feeds the decoded message
straight into the handler pipeline.

```csharp
opts.PublishMessage<OrderPlaced>()
    .ToPulsarTopic("persistent://public/default/orders")
    .UseAvroSchema<OrderPlaced>();

opts.ListenToPulsarTopic("persistent://public/default/orders")
    .UseAvroSchema<OrderPlaced>();
```

`T` must be an Apache.Avro **`ISpecificRecord`** (the classes generated by `avrogen` from an `.avsc`
schema, or a hand-written record exposing a static `_SCHEMA`). Protobuf and other schema types can be
plugged in the same way through a custom codec behind `UsePulsarSchema(...)`.

## Producer Deduplication <Badge type="tip" text="6.8" />

Pulsar can deduplicate messages **at the broker** using a per-producer sequence id, so a message that is
sent more than once — most commonly an outbox **resend** of the same envelope after a transient failure —
is stored only once. Opt in on a sending endpoint with `EnableDeduplication()`:

```csharp
opts.PublishMessage<OrderPlaced>()
    .ToPulsarTopic("persistent://public/default/orders")
    .EnableDeduplication();
```

Wolverine creates the producer with a **stable producer name** and stamps a monotonic sequence id per
message, reusing the same sequence id when the *same envelope* is sent again so the broker discards the
duplicate. A brand-new message always gets a new id and is delivered normally.

You must also enable deduplication on the broker side (it is off by default), e.g.:

```bash
pulsar-admin namespaces set-deduplication public/default --enable
# or per topic:
pulsar-admin topics set-deduplication-status persistent://public/default/orders --enable
```

::: warning Producer→broker only — not end-to-end exactly-once
This suppresses duplicates on the **produce** side; it is not a transactional read-process-write
exactly-once engine (a deliberate non-goal for the transport — and Pulsar transactions are not exposed by
the DotPulsar client). Deduplication keys on `(producer name, sequence id)`, so pass a fixed
`EnableDeduplication("my-producer")` name if you need it to hold across process restarts.
:::

## Interoperability

::: tip
Also see the more generic [Wolverine Guide on Interoperability](/tutorials/interop)
:::

Pulsar interoperability is done through the `IPulsarEnvelopeMapper` interface.

## URI reference

The `PulsarEndpointUri` helper class produces Wolverine endpoint URIs of the form `pulsar://persistent/{tenant}/{ns}/{topic}` or `pulsar://non-persistent/{tenant}/{ns}/{topic}` — the form Wolverine's parser accepts. Pulsar-native topic-path strings (`persistent://...`) used by the native Pulsar client are a separate concept and are not built by this helper.

| Helper call | Resulting URI |
|---|---|
| `PulsarEndpointUri.PersistentTopic("public", "default", "orders")` | `pulsar://persistent/public/default/orders` |
| `PulsarEndpointUri.NonPersistentTopic("public", "default", "orders")` | `pulsar://non-persistent/public/default/orders` |
| `PulsarEndpointUri.Topic("public", "default", "orders", persistent: true)` | `pulsar://persistent/public/default/orders` |
| `PulsarEndpointUri.Topic("persistent://public/default/orders")` | `pulsar://persistent/public/default/orders` |

```csharp
using Wolverine.Pulsar;

var uri = PulsarEndpointUri.PersistentTopic("public", "default", "orders");
```
