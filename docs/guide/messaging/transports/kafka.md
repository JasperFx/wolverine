# Using Kafka

::: warning
The Kafka transport does not really support the "Requeue" error handling policy in Wolverine. "Requeue" in this case becomes
effectively an inline "Retry"
:::

## Installing

To use [Kafka](https://www.confluent.io/what-is-apache-kafka/) as a messaging transport with Wolverine, first install the `WolverineFx.Kafka` library via nuget to your project. Behind the scenes, this package uses the [Dekaf client library](https://thomhurst.github.io/Dekaf/) — a pure C# Kafka client with no native librdkafka dependency — for accessing Kafka brokers. Because Dekaf is fully managed .NET, the transport is also friendly to Native AOT deployments.

```bash
dotnet add package WolverineFx.Kafka
```

::: warning Kafka 4.0+ broker required
The Dekaf client speaks only the [KIP-848](https://cwiki.apache.org/confluence/display/KAFKA/KIP-848%3A+The+Next+Generation+of+the+Consumer+Rebalance+Protocol) consumer-group protocol, so the Kafka transport requires a **Kafka 4.0 or later** broker. Wolverine's own docker-compose broker (`confluentinc/confluent-local:8.0.x`) satisfies this. Against an older broker the consumers cannot join their groups.
:::

## Aspire Integration

::: tip
See the full [Aspire + Wolverine Kafka sample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/AspireWithKafka) for a working end-to-end example, and
[Aspire Integration](/guide/aspire) for how the named connection strings work across every Wolverine transport and
message store.
:::

The `UseKafkaUsingNamedConnection()` overload reads the Kafka bootstrap servers from `IConfiguration.GetConnectionString()`.
.NET Aspire injects this automatically when you use `.WithReference()` in the AppHost:

**AppHost:**
```csharp
// Aspire.Hosting.Kafka NuGet package
var kafka = builder.AddKafka("kafka")
    .WithKafkaUI();

builder.AddProject<Projects.MyWorker>("worker")
    .WithReference(kafka)
    // WaitFor ensures Kafka is healthy before your service starts,
    // so AutoProvision() will always succeed.
    .WaitFor(kafka);
```

**Service project:**
```csharp
// WolverineFx.Kafka NuGet package
builder.UseWolverine(opts =>
{
    opts.UseKafkaUsingNamedConnection("kafka")
        // AutoProvision creates all declared topics at startup.
        // This works reliably because Aspire's WaitFor() guarantees
        // Kafka is healthy before the service starts.
        .AutoProvision();

    opts.PublishMessage<MyMessage>().ToKafkaTopic("my-topic");
    opts.ListenToKafkaTopic("my-topic").ProcessInline();
});
```

You can still pass optional `configureConsumers` / `configureProducers` callbacks for fine-tuning:

```csharp
opts.UseKafkaUsingNamedConnection("kafka",
    configureConsumers: c => c.GroupId = "my-service",
    configureProducers: p => p.Linger = TimeSpan.FromMilliseconds(5))
    .AutoProvision();
```

```warning
The configuration in `ConfigureConsumer()` for each topic completely overwrites any previous configuration
```

To connect to Kafka, use this syntax:

<!-- snippet: sample_bootstrapping_with_kafka -->
<a id='snippet-sample_bootstrapping_with_kafka'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseKafka(KafkaContainerFixture.ConnectionString)

            // See https://github.com/confluentinc/confluent-kafka-dotnet for the exact options here
            .ConfigureClient(client =>
            {
                // configure both producers and consumers

            })

            .ConfigureConsumers(consumer =>
            {
                // configure only consumers
            })

            .ConfigureProducers(producer =>
            {
                // configure only producers
            })
            
            .ConfigureProducerBuilders(builder =>
            {
                // there are some options that are only exposed
                // on the ProducerBuilder
            })
            
            .ConfigureConsumerBuilders(builder =>
            {
                // there are some Kafka client options that
                // are only exposed from the builder
            })
            
            .ConfigureAdminClientBuilders(builder =>
            {
                // configure admin client builders
            });

        // Just publish all messages to Kafka topics
        // based on the message type (or message attributes)
        // This will get fancier in the near future
        opts.PublishAllMessages().ToKafkaTopics();

        // Or explicitly make subscription rules
        opts.PublishMessage<ColorMessage>()
            .ToKafkaTopic("colors")
            
            // Fine tune how the Kafka Topic is declared by Wolverine
            .Specification(spec =>
            {
                spec.NumPartitions = 6;
                spec.ReplicationFactor = 3;
            })
            
            // OR, you can completely control topic creation through this:
            .TopicCreation(async (client, topic) =>
            {
                topic.Specification.NumPartitions = 8;
                topic.Specification.ReplicationFactor = 2;
                
                // You do have full access to the IAdminClient to do
                // whatever you need to do

                await client.CreateTopicsAsync([topic.Specification]);
            })
            
            // Override the producer configuration for just this topic
            .ConfigureProducer(config =>
            {
                config.BatchSize = 100;
                config.EnableGaplessGuarantee = true;
                config.EnableIdempotence = true;
            });

        // Listen to topics
        opts.ListenToKafkaTopic("red")
            .ProcessInline()
            
            // Override the consumer configuration for only this 
            // topic
            // This is NOT combinatorial with the ConfigureConsumers() call above
            // and completely replaces the parent configuration
            .ConfigureConsumer(config =>
            {
                // This will also set the Envelope.GroupId for any
                // received messages at this topic
                config.GroupId = "foo";
                config.BootstrapServers = KafkaContainerFixture.ConnectionString;

                // Other configuration
            })
            // Extends the consumer configuration for this topic only.
            // Unlike ConfigureConsumer(), this preserves any existing topic-level
            // ConsumerConfig and only applies the changes below.
            .ExtendConsumerConfiguration(config =>
            {
                // This also sets Envelope.GroupId for any messages received
                // from this topic.
                config.GroupId = "foo";
                config.BootstrapServers = KafkaContainerFixture.ConnectionString;

                // Other additive configuration
            })
            // Configure circuit breaker behavior for
            // this specific Kafka listener
            .CircuitBreaker(cb =>
            {
                cb.MinimumThreshold = 10;
                cb.PauseTime = TimeSpan.FromMinutes(1);
            })
            // Fine tune how the Kafka Topic is declared by Wolverine
            .Specification(spec =>
            {
                spec.NumPartitions = 6;
                spec.ReplicationFactor = 3;
            });

        opts.ListenToKafkaTopic("green")
            .BufferedInMemory();

        // This will direct Wolverine to try to ensure that all
        // referenced Kafka topics exist at application start up
        // time
        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L15-L144' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_kafka' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `ConfigureClient()` / `ConfigureConsumers()` / `ConfigureProducers()` methods work against Wolverine-owned
option classes in the `Wolverine.Kafka` namespace — `KafkaClientOptions`, `KafkaConsumerOptions`, and
`KafkaProducerOptions` (plus `KafkaAdminClientOptions` for `AutoProvision(...)`). The shared `KafkaClientOptions`
base covers connectivity and security (`BootstrapServers`, `ClientId`, `SecurityProtocol`, `SaslMechanism`,
`SaslUsername`, `SaslPassword`), consumer options cover the group and fetch settings (`GroupId`,
`GroupInstanceId`, `AutoOffsetReset`, `EnableAutoCommit`, `EnableAutoOffsetStore`, `AutoCommitInterval`,
`IsolationLevel`, `MaxPollRecords`, `FetchMaxBytes`, `MaxPartitionFetchBytes`, `SessionTimeout`,
`HeartbeatInterval`), and producer options cover delivery behavior (`EnableIdempotence`, `Acks`, `Linger`,
`BatchSize`). Anything beyond these curated options is reachable through `ConfigureConsumerBuilders()` /
`ConfigureProducerBuilders()` / `ConfigureAdminClientBuilders()`, which hand you Dekaf's fluent
`ConsumerBuilder<string, byte[]>` / `ProducerBuilder<string, byte[]>` / `AdminClientBuilder` directly.

## Listener Consumer Settings <Badge type="tip" text="5.16" />

When building a Kafka listener, Wolverine configures the underlying `KafkaConsumerOptions` differently
depending on whether the listener endpoint is **durable** (backed by the transactional inbox) and how the listener
processes messages. Understanding these settings is important for getting the delivery guarantees you need.

### How Endpoint Mode Affects Consumer Configuration

When an endpoint uses `EndpointMode.Durable` (i.e., you've called `.UseDurableInbox()` or applied durable inbox
globally), Wolverine overrides the following consumer setting before building the listener:

| Consumer Setting | Durable (`UseDurableInbox`) | Non-Durable (`BufferedInMemory` / `Inline`) |
|---|---|---|
| `EnableAutoCommit` | `false` | `true` (Kafka default) |
| `EnableAutoOffsetStore` | `true` (Kafka default) | `true` (Kafka default) |

In **durable mode**, Wolverine disables Kafka's automatic offset *commit* so that offsets are only committed
when Wolverine explicitly calls `Commit()` after a message has been successfully persisted to the transactional
inbox. The Kafka client still auto-stores the offset on each `Consume()` call (the default behavior), which
tracks the consumer's position. However, the stored offset is not pushed to the broker until `Commit()` is
called. This gives correct at-least-once semantics -- if the application shuts down unexpectedly before
committing, unprocessed messages will be re-delivered when the consumer rejoins the group.

In **non-durable mode** (`BufferedInMemory` or `ProcessInline`), Kafka's default auto-commit behavior is left
in place. The Kafka client library periodically commits offsets automatically, which provides higher throughput
at the cost of potential message loss during an ungraceful shutdown.

### Offset Commit Behavior in the Listener

The `KafkaListener` advances the consumer offset (commits the *specific* `TopicPartitionOffset` of the
message, never the consumer's global position) in these situations:

- **On successful processing** -- `CompleteAsync()` stores/commits the message's offset after it finishes
  processing. In durable mode this is the path that advances the offset.
- **On poison pill messages** -- If an incoming Kafka message cannot be deserialized into a Wolverine envelope
  at all (a true poison pill), the listener advances past that message's offset to skip the bad message and
  avoid blocking the consumer.
- **On dead letter queue routing** -- When a message exhausts all retries and is moved to the native dead letter
  queue topic, its offset is advanced after the DLQ produce succeeds.

### Commit Strategy <Badge type="tip" text="6.8" />

How and when those offsets are flushed to the broker is controlled by `CommitMode`. The default,
`StoreThenAutoFlush`, is the idiomatic high-throughput Kafka model: each processed offset is *stored*
locally (`EnableAutoOffsetStore = false` plus an explicit store per completed message) and the client's
background committer flushes them on `AutoCommitInterval`. There is **no** synchronous broker round trip
per message.

```csharp
opts.ListenToKafkaTopic("orders")
    // The default — non-blocking, at-least-once, idiomatic high throughput
    .CommitOffsets(CommitMode.StoreThenAutoFlush);

opts.ListenToKafkaTopic("strict")
    // Synchronously commit each message as it completes (strict at-least-once, lowest throughput)
    .CommitOffsets(CommitMode.PerMessage);

opts.ListenToKafkaTopic("bulk")
    // Wolverine commits the contiguous offset watermark every N messages...
    .CommitOffsetsAfterCount(500);

opts.ListenToKafkaTopic("bulk2")
    // ...or every elapsed interval. Neither commits ahead of the lowest in-flight offset.
    .CommitOffsetsAfterInterval(TimeSpan.FromSeconds(2));
```

If you explicitly set `EnableAutoCommit = true` via `ConfigureConsumer`, Wolverine suppresses its own manual
commits and leaves offset management entirely to the Kafka client. Pending/stored offsets are flushed on a
graceful shutdown so progress is not lost.

::: tip In-flight–safe under concurrency
All three manual commit strategies (`StoreThenAutoFlush`, `PerMessage`, and the batch modes) route through a
per-partition watermark, so when a listener processes messages concurrently (the default buffered mode runs
up to `MaxDegreeOfParallelism` handlers at once) and a later offset finishes before an earlier one, the
committed/stored position **never advances past a message that is still in flight**. The watermark also makes
no assumption that offsets are contiguous, so it behaves correctly on compacted topics and on transactional
topics read with `read_committed`, where the broker hands out offset gaps. As always, the strongest
crash-safety still comes from the durable inbox (see _Idempotency &amp; Exactly-Once_ below).
:::

### Recommended Configuration by Use Case

**At-least-once delivery** (recommended for most use cases):

```csharp
opts.ListenToKafkaTopic("orders")
    .UseDurableInbox();
```

This ensures messages are persisted to the inbox before the offset is committed. If your process crashes, the
message will be re-delivered by Kafka and de-duplicated by Wolverine's inbox.

**Higher throughput, at-most-once delivery**:

```csharp
opts.ListenToKafkaTopic("telemetry")
    .BufferedInMemory();
```

With auto-commit enabled, offsets may be committed before processing completes. This is suitable for
high-volume, loss-tolerant workloads like telemetry or logging.

**Inline processing with manual consumer tuning**:

```csharp
opts.ListenToKafkaTopic("events")
    .ProcessInline()
    .ConfigureConsumer(config =>
    {
        config.EnableAutoCommit = false;
        config.AutoOffsetReset = AutoOffsetReset.Earliest;
    });
```

You can always override any consumer setting per-topic using `ConfigureConsumer()`. Note that this
**completely replaces** the parent-level consumer configuration -- it is not combinatorial.

## Scaling Out / Concurrency <Badge type="tip" text="6.8" />

The Kafka-native way to scale out message processing is to **run more nodes in the same consumer group**.
Kafka's own group coordinator assigns the topic's partitions across the live consumers in the group and
guarantees that only one consumer processes a given partition at a time, so you get safe, ordered,
horizontally-scaled processing for free. This is the recommended approach for Kafka — reach for it before
in-process parallelism.

The ceiling is the **partition count**: a topic with _N_ partitions can be processed by at most _N_ nodes
concurrently (extra nodes sit idle as hot standbys). Size your partition count for your target throughput
and node count.

Rebalancing itself needs no configuration — the transport always runs on Kafka's next-generation
consumer rebalance protocol ([KIP-848](#rebalance-protocol-kip-848), below), so a rebalance keeps each
consumer's unaffected partitions instead of a stop-the-world revoke-everything cycle. One **opt-in**
consumer setting makes the assignment even more stable in production:

```csharp
opts.UseKafka(connectionString)
    // Static membership: rolling restarts/deploys of the same node don't trigger partition churn.
    // The group.instance.id defaults to POD_NAME, then HOSTNAME, then the machine name.
    .UseStaticMembership();
```

Also available per-listener on `ListenToKafkaTopic(...).UseStaticMembership(...)`.

::: warning group.instance.id must be unique per node and stable across restarts
Static membership only works when each node uses a **distinct** `group.instance.id` that **stays the same**
across restarts of that node. Two nodes sharing one id makes Kafka treat them as a single member and fence
one out — silently losing messages. The default resolution (`POD_NAME` → `HOSTNAME` → machine name) matches
the k8s `StatefulSet` idiom; supply your own when those aren't suitable:

```csharp
.UseStaticMembership(() => Environment.GetEnvironmentVariable("MY_INSTANCE"))
```

Wolverine logs the resolved `group.instance.id` at startup so you can verify per-node uniqueness, and warns
if no stable value could be resolved. Avoid a single hard-coded literal applied to every node.
:::

### Rebalance Protocol (KIP-848)

The Kafka transport is **KIP-848-only**: the Dekaf client speaks only the
[next-generation consumer rebalance protocol](https://cwiki.apache.org/confluence/display/KAFKA/KIP-848%3A+The+Next+Generation+of+the+Consumer+Rebalance+Protocol),
which requires a **Kafka 4.0+ broker** (it is GA and enabled by default on 4.0+). Under KIP-848 the group
coordinator computes assignments server side and reconciles members one partition at a time over the
heartbeat — rebalances are always **broker-driven and incremental**, with no stop-the-world
JoinGroup/SyncGroup barrier and no client-side assignor to choose. The server-side assignor is chosen with
the broker/group config `group.remote.assignor` (`uniform` by default, or `range`).

Because incremental, cooperative-style rebalancing is inherent to the protocol, the older opt-ins
`UseCooperativeStickyAssignment()` and `UseNextGenerationRebalanceProtocol()` are now marked `[Obsolete]`
and are **no-ops** — calling them is harmless, but they configure nothing and can simply be deleted.
Static membership (`UseStaticMembership(...)` / `group.instance.id`) is unchanged, fully supported under
KIP-848, and remains the right tool for churn-free rolling restarts.

### By-Key Concurrency Within a Partition <Badge type="tip" text="6.8" />

This is the **second** concurrency lever, not the first.

1. **First, scale out natively** — add partitions and run more nodes in the same consumer group (above).
   Kafka routes same-key messages to the same partition, so ordering is free up to the partition count.
   Reaching for in-partition concurrency *before* adding partitions is usually a smell.
2. **Then**, when you have a hot partition or can't add more partitions, process **different keys
   concurrently within a single partition** while keeping strict ordering per key:

```csharp
opts.ListenToKafkaTopic("orders")
    .ProcessConcurrentlyByKey(PartitionSlots.Five);
```

Within each partition assigned to this node, messages are sharded across the configured number of slots by
their **Kafka message key** — same key → same slot (strictly ordered), different keys → different slots
(concurrent). To group by a business field instead of the raw Kafka key, configure
[message partitioning rules](/guide/messaging/partitioning).

This runs in **durable** mode: the Kafka offset is committed as each message is persisted to the inbox in
consumption order, and the inbox processing is then sharded by key. That **decouples offset commit from
out-of-order completion** — if key A (offset 5) is still running when key B (offset 6) finishes, the inbox
owns both, so a crash or rebalance can't lose A. Pairs naturally with KIP-848's incremental rebalancing
(above), which keeps a rebalance from disrupting unaffected partitions.

### Cold Start vs. Live Tail <Badge type="tip" text="6.8" />

`auto.offset.reset` controls where a consumer **starts** when its group has **no committed offset** for a
partition — i.e. a cold start. Once the group has committed an offset, it resumes from there and this
setting is ignored. It is *not* a replay switch.

```csharp
opts.ListenToKafkaTopic("orders").BeginAtEarliest();   // cold start from the beginning of the topic
opts.ListenToKafkaTopic("orders").BeginAtLatest();     // cold start from the tail (skip the backlog)
```

Both are also available as a transport-wide default (`opts.UseKafka(...).BeginAtEarliest()`).

::: warning This only affects the *first* read of a partition by a group
If the consumer group already has a committed offset, `BeginAtEarliest()`/`BeginAtLatest()` do nothing —
the group resumes from its committed position. To genuinely re-read old data you need a new group id or an
explicit seek/replay (a separate, bounded operation).
:::

#### Hot-tail / broadcast consume

Sometimes you want **every node** to see **every message** as it arrives — live dashboards, cache
invalidation, fan-out-to-all-instances — rather than the competing-consumer model where each message goes
to exactly one node in the group. Use `TailFromLatest()`:

```csharp
opts.ListenToKafkaTopic("live-events").TailFromLatest();
```

Each process joins a **unique, ephemeral consumer group** and starts at the tail, so every node receives all
messages, never replays old data, and commits nothing. This is the idiomatic Kafka pattern for broadcast.

A few things to know:

- Because it starts at the tail, only messages published **after** a node has joined and been assigned its
  partitions are delivered — there is no backlog replay.
- Each process creates a transient consumer-group entry on the broker; Kafka expires these automatically via
  `offsets.retention.minutes`. Harmless, but worth knowing for cluster operators.
- Reach for `TailFromLatest()` when you want **all** nodes to process each message; use a normal
  shared-group listener (the default) when you want each message processed **once** across the cluster.

## Replaying a Topic <Badge type="tip" text="6.8" />

When you need to **reprocess** a window of a topic's history — error recovery, rebuilding downstream
state, replaying after a bug fix — Wolverine offers a **bounded, one-shot replay** that reads a range of a
topic back through the **normal handler pipeline**. It uses a throwaway `Assign()`-based consumer that
joins **no consumer group** and **never commits offsets**, so steady-state consumption is completely
untouched.

```csharp
// Programmatic API on IHost
await host.ReplayKafkaTopicAsync(new KafkaReplayRequest
{
    Topic = "orders",
    FromTimestamp = DateTimeOffset.UtcNow.AddHours(-1),  // or FromOffset = 1500
    // ToTimestamp / ToOffset optional — defaults to "now" (the current high-water mark)
    // Partitions = [0, 1]                                // optional subset; defaults to all
});
```

Start defaults to the beginning of each partition and end defaults to the current high-water mark, so
omitting the bounds replays the whole topic as it stands. Timestamps are resolved to offsets per partition
via Kafka's `OffsetsForTimes`.

There is also a CLI verb wrapping the same API:

```bash
dotnet run -- kafka-replay orders --from-timestamp 2026-06-18T12:00:00Z
dotnet run -- kafka-replay orders --from-offset 1500 --to-offset 2000 --partitions 0,1
```

::: warning Replayed messages are re-handled
Each replayed record flows through your handlers again, exactly like live consumption. Handlers should be
**idempotent** (the same expectation as any at-least-once reprocessing). If you use the durable inbox,
replayed envelopes pass through the same inbox + de-duplication path.
:::

Because the replay consumer joins no group, it needs no consumer group ACL on brokers that grant groups by
prefix (the usual Confluent Cloud setup). Replayed envelopes carry the `Destination` the live listener would
have stamped: the topic's own Uri, or that of a `ListenToKafkaTopics(...)` group subscribing to the topic.

Replay reads forward to the end boundary and stops cleanly. It is a discrete operation — for *live* seek of
a running listener, or a CritterWatch control-pane, see the follow-up issues.

## Idempotency & Exactly-Once with Kafka <Badge type="tip" text="6.8" />

Kafka delivery is **at-least-once** by default: a consumer can see a message more than once (after a
rebalance, a crash before the offset is committed, or a [replay](#replaying-a-topic)). There are two very
different ways to get "exactly-once-ish" behavior, and for most Wolverine users the first one is the answer.

### Recommended for database-backed apps: Wolverine's durable inbox/outbox

If your handlers touch a database, use Wolverine's [durable inbox/outbox](/guide/durability/). The incoming
message and its side effects commit in **one database transaction** (inbox), and outgoing messages commit in
the **same transaction** as your business state (outbox) before being forwarded. The inbox **de-duplicates**
redelivered messages, so your handlers are safe under at-least-once delivery:

```csharp
opts.ListenToKafkaTopic("orders").UseDurableInbox();
```

This gives you effectively-once processing that **spans your database and Kafka** — something Kafka
transactions alone cannot do, because they can't enlist an external database. This is how most Wolverine
applications should get exactly-once-style guarantees; you do **not** need Kafka transactions for it.

::: tip Records from producers that aren't Wolverine
The inbox recognizes a redelivery by its envelope id, and keeps a handled message's row for
`DurabilitySettings.KeepAfterMessageHandling` (five minutes by default) to catch one. A record published by
Wolverine carries its id in the `id` header. A record from any other producer, raw JSON or a binary-mode
CloudEvent for example, has none, so a durable listener derives the id from where the record sits in the log:
the broker, the tenant, the consumer group, the topic, the partition and the offset. Every read of that record
gets the same id, so a rewind, a rebalance or a listener restart that hands it over again is discarded as a
duplicate, not handled twice. An `id` header, or an id that your own `IKafkaEnvelopeMapper` assigns, still
takes precedence. Two consumer groups are two independent subscriptions, and each handles the record once.
Buffered and inline listeners have no inbox to deduplicate against, and keep a fresh id per read.

With `EnableInboxPartitioning` the same holds: the inbox looks for the handled row in its own partition, so a
redelivery that arrives after the first copy was handled is discarded too. See
[PostgreSQL](/guide/durability/postgresql) for the one timing window partitioning leaves open.

If your inbox is in SQL Server, be aware that these derived ids are effectively random as far as SQL Server's
clustered index is concerned, so a high volume of non-Wolverine records will fragment the inbox table. See
[Optimized Envelope IDs for SQL Server](/guide/durability/sqlserver#optimized-envelope-ids-for-sql-server).

A *structured-mode* CloudEvent (the whole CloudEvents document is the record value) whose `id` isn't a GUID
does **not** get this protection today. See [Deduplicating CloudEvents](/tutorials/interop#deduplicating-cloudevents).
:::

### Idempotent producer

Dekaf producers are idempotent by default, so producer-side retries can't write duplicates to the broker.
To pin that on explicitly, for example over configuration that sets `EnableIdempotence = false`:

```csharp
opts.UseKafka(connectionString).UseIdempotentProducer();       // node-wide
opts.PublishMessage<T>().ToKafkaTopic("t").UseIdempotentProducer();  // per topic
```

This sets `EnableIdempotence = true` on the producer options (which implies acks from all in-sync replicas
and bounded in-flight requests). It is
**producer→broker** de-duplication only — it does not make consume-process-produce atomic. Set
`EnableIdempotence = false` through `ConfigureProducers(...)` to turn it off.

### `read_committed` isolation

When you consume a topic that is written by Kafka transactions, set the consumer to skip records from
aborted transactions:

```csharp
opts.UseKafka(connectionString).UseReadCommitted();             // node-wide
opts.ListenToKafkaTopic("orders").UseReadCommitted();           // per listener
```

The default is `read_uncommitted`.

### Handler idempotency

Because delivery is at-least-once, **design your handlers to tolerate redelivery** — especially if you
don't use the durable inbox, and always when using [retry topics](#) or [replay](#replaying-a-topic). Make
writes idempotent (upserts keyed by a business id, conditional updates, dedupe tables), so reprocessing the
same message is harmless.

### Non-goal: a transactional read-process-write EOS engine

Wolverine does **not** implement a Kafka transactional read-process-write engine (`transactional.id` +
`Begin/Commit/AbortTransaction` + `SendOffsetsToTransaction` to make consume→transform→produce→commit-offset
atomic inside Kafka). That mode bypasses both the durable inbox and Wolverine's commit strategy, and only
adds value for **DB-free Kafka→Kafka** pipelines — which are better served by Kafka Streams. Wolverine stays
in the message-bus + database-outbox lane; if you need pure in-Kafka transactional exactly-once, reach for
Kafka Streams.

## Publishing by Partition Key

To publish messages with Kafka using a designated [partition key](https://developer.confluent.io/courses/apache-kafka/partitions/), use the
`DeliveryOptions` to designate a partition like so:

<!-- snippet: sample_publish_to_kafka_by_partition_key -->
<a id='snippet-sample_publish_to_kafka_by_partition_key'></a>
```cs
public static ValueTask publish_by_partition_key(IMessageBus bus)
{
    return bus.PublishAsync(new Message1(), new DeliveryOptions { PartitionKey = "one" });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/when_publishing_and_receiving_by_partition_key.cs#L14-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_kafka_by_partition_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Propagating GroupId to PartitionKey <Badge type="tip" text="5.17" />

By default, Wolverine stamps the Kafka consumer's configured `GroupId` onto the `GroupId` property of every incoming
envelope. If your handler produces cascaded messages that should land on the same partition, you can enable automatic
propagation of the originating `GroupId` to the outgoing `PartitionKey`:

```csharp
opts.Policies.PropagateGroupIdToPartitionKey();
```

This eliminates the need to manually set `DeliveryOptions.PartitionKey` on every outgoing message from your handlers.
The rule will never override an explicitly set `PartitionKey`. See the [Partitioned Sequential Messaging](/guide/messaging/partitioning#propagating-groupid-to-partitionkey)
documentation for more details and a code sample.

::: warning
When using `PropagateGroupIdToPartitionKey()` together with business-level partition key derivation (e.g.
`UseInferredMessageGrouping().ByPropertyNamed(...)`), you should disable consumer group ID stamping on your listeners.
Otherwise the consumer group name (e.g. `"my-application-name"`) will be written to `envelope.GroupId` and may
pollute the partition key derivation for cascaded messages:

```csharp
opts.ListenToKafkaTopic("my-topic")
    .DisableConsumerGroupIdStamping()
    .ConfigureConsumer(config =>
    {
        config.GroupId = "my-application-name";
    });
```
:::

### Disabling Consumer Group ID Stamping

If you do not want the Kafka consumer group name written to `envelope.GroupId` at all, call
`DisableConsumerGroupIdStamping()` on the listener:

```csharp
opts.ListenToKafkaTopic("orders")
    .ProcessInline()
    .DisableConsumerGroupIdStamping();
```

The same method is available on `ListenToKafkaTopics()` (multi-topic listeners).

## Interoperability

::: tip
Also see the more generic [Wolverine Guide on Interoperability](/tutorials/interop)
:::

It's a complex world out there, and it's more than likely you'll need your Wolverine application to interact with system
that aren't also Wolverine applications. First, know that the transport is fully wire-compatible with
applications using Confluent.Kafka or any other Kafka client — keys, values, headers, timestamps, and
tombstones all round-trip cleanly in both directions. Beyond that, it's possible to send or receive raw JSON
through Kafka and Wolverine by using the options shown below in test harness code:

<!-- snippet: sample_raw_json_sending_and_receiving_with_kafka -->
<a id='snippet-sample_raw_json_sending_and_receiving_with_kafka'></a>
```cs
_receiver = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        //opts.EnableAutomaticFailureAcks = false;
        opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.ListenToKafkaTopic("json")

            // You do have to tell Wolverine what the message type
            // is that you'll receive here so that it can deserialize the
            // incoming data
            .ReceiveRawJson<ColorMessage>();

        // Include test assembly for handler discovery
        opts.Discovery.IncludeAssembly(GetType().Assembly);

        opts.Services.AddResourceSetupOnStartup();

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka");

        opts.Services.AddResourceSetupOnStartup();

        opts.Policies.UseDurableInboxOnAllListeners();
    }).StartAsync();

_sender = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.Policies.DisableConventionalLocalRouting();

        opts.Services.AddResourceSetupOnStartup();

        opts.PublishAllMessages().ToKafkaTopic("json")
            
            // Just publish the outgoing information as pure JSON
            // and no other Wolverine metadata
            .PublishRawJson(new JsonSerializerOptions());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/publish_and_receive_raw_json.cs#L22-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_raw_json_sending_and_receiving_with_kafka' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Wolverine versions from 5.0 up to and including 6.19 had a bug where `ReceiveRawJson()` and `PublishRawJson()`
silently ran the *default* Kafka envelope mapper instead of the raw-JSON mapper. If you are upgrading, be aware
of these behavioral corrections:

* **Incoming records no longer promote Wolverine's reserved header names.** Previously, a record header named
  `tenant-id`, `saga-id`, `id`, `correlation-id`, etc. was mapped straight onto the matching `Envelope` property —
  meaning any external producer could set the tenant id or saga identity of your messages. These reserved names
  are now deliberately ignored on raw-JSON listeners. All *other* record headers are copied into
  `Envelope.Headers`, and `Envelope.SentAt` is read from the Kafka record timestamp.
* **Outgoing records are now genuinely raw.** `PublishRawJson()` previously stamped the full set of Wolverine
  protocol headers (`message-type`, `correlation-id`, and friends) onto every record. Those headers are no longer
  written. When no `JsonSerializerOptions` are passed, the body bytes are unchanged — messages are still serialized
  by the endpoint's JSON serializer (camel-cased property names by default), so downstream consumers parsing the
  JSON are unaffected.
* **The `JsonSerializerOptions` argument now works.** Previously the options passed to either method were silently
  ignored on every code path. Supplying options now sets that endpoint's default JSON serialization — driving
  deserialization of incoming bodies on `ReceiveRawJson()` (e.g. snake_cased payloads via
  `PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower`) and serialization of outgoing bodies on
  `PublishRawJson()`. Omitting the argument keeps Wolverine's defaults (camel-cased, case-insensitive reads),
  so existing callers see no change.

If you have a *trusted* upstream producer that intentionally sets `tenant-id` (or another reserved header) and you
depend on that promotion, register a custom mapper with `UseInterop((runtime, endpoint) => ...)` in place of
`ReceiveRawJson()` to keep that behavior as an explicit opt-in.
:::

## Confluent Schema Registry Serializers <Badge type="tip" text="5.27" />

When you need to interoperate with other Kafka clients that use the [Confluent Schema Registry](https://docs.confluent.io/platform/current/schema-registry/index.html)
wire format, Wolverine provides built-in serializers for both **JSON Schema** and **Avro** that integrate directly with
the Schema Registry. These serializers handle schema registration, evolution, and the Confluent wire format
(magic byte + 4-byte schema ID prefix) automatically. They are built on Dekaf's Schema Registry packages,
and the registry client is built with `Dekaf.SchemaRegistry`:

```csharp
using Dekaf.SchemaRegistry;

var schemaRegistry = new SchemaRegistryClient(
    new SchemaRegistryConfig { Url = "http://localhost:8081" });
```

### JSON Schema Serializer

The `SchemaRegistryJsonSerializer` works with any .NET class — no code generation or special interfaces
required. The JSON Schema for each message type is generated from the type itself via System.Text.Json's
`JsonSchemaExporter`:

```csharp
using Dekaf.SchemaRegistry;
using Wolverine.Kafka.Serialization;

var schemaRegistry = new SchemaRegistryClient(
    new SchemaRegistryConfig { Url = "http://localhost:8081" });

opts.PublishMessage<OrderPlaced>()
    .ToKafkaTopic("orders")
    .DefaultSerializer(new SchemaRegistryJsonSerializer(schemaRegistry));

opts.ListenToKafkaTopic("orders")
    .DefaultSerializer(new SchemaRegistryJsonSerializer(schemaRegistry));
```

You can also pass a `JsonSchemaSerializerConfig` to control settings like the subject name strategy
(`TopicName`, `RecordName`, or `TopicRecordName`), and optionally a `JsonSerializerOptions` to control how
the message body itself is serialized:

```csharp
var serializer = new SchemaRegistryJsonSerializer(schemaRegistry,
    new JsonSchemaSerializerConfig
    {
        SubjectNameStrategy = SubjectNameStrategy.TopicRecordName
    });
```

### Avro Serializer

The `SchemaRegistryAvroSerializer` works with Avro-generated classes that implement `Avro.Specific.ISpecificRecord`.
Use the [Apache Avro tools](https://avro.apache.org/docs/current/getting-started-csharp/) or the
[Confluent avrogen tool](https://docs.confluent.io/platform/current/schema-registry/serdes-develop/serdes-avro.html)
to generate C# classes from `.avsc` schema files. An optional `Dekaf.SchemaRegistry.Avro.AvroSerializerConfig`
controls settings like the subject name strategy:

```csharp
using Dekaf.SchemaRegistry;
using Wolverine.Kafka.Serialization;

var schemaRegistry = new SchemaRegistryClient(
    new SchemaRegistryConfig { Url = "http://localhost:8081" });

opts.PublishMessage<OrderPlacedAvro>()
    .ToKafkaTopic("orders-avro")
    .DefaultSerializer(new SchemaRegistryAvroSerializer(schemaRegistry));

opts.ListenToKafkaTopic("orders-avro")
    .DefaultSerializer(new SchemaRegistryAvroSerializer(schemaRegistry));
```

### How It Works

Both serializers extend the `SchemaRegistrySerializer` base class which implements Wolverine's
`IAsyncMessageSerializer` interface, so schema registration and lookup against the registry are awaited on
Wolverine's normal send and receive paths rather than blocking a thread. Internally, each serializer creates
and caches a typed serializer per message type on first use, and delegates all schema registration and lookup
to the Dekaf Schema Registry client (`Dekaf.SchemaRegistry.ISchemaRegistryClient`).

The serialized bytes use the standard [Confluent wire format](https://docs.confluent.io/platform/current/schema-registry/fundamentals/serdes-develop/index.html#wire-format):
a magic byte (`0x00`), followed by a 4-byte big-endian schema ID, followed by the payload. This makes your
Wolverine producers and consumers fully compatible with any other Kafka client that uses the Schema Registry
(Java, Python, Go, applications using the Confluent serdes, etc.).

::: tip
The `ReadFromData(byte[])` overload (without a message type) is **not supported** by these serializers because
the Schema Registry wire format does not embed the .NET type name. Wolverine must know the expected message type,
which is handled automatically when you configure typed listeners.
:::

## Instrumentation & Diagnostics <Badge type="tip" text="3.13" />

When receiving messages through Kafka and Wolverine, there are some useful elements of Kafka metadata
on the Wolverine `Envelope` you can use for instrumentation or diagnostics as shown in this sample middleware:

<!-- snippet: sample_kafkainstrumentation_middleware -->
<a id='snippet-sample_kafkainstrumentation_middleware'></a>
```cs
public static class KafkaInstrumentation
{
    // Just showing what data elements are available to use for 
    // extra instrumentation when listening to Kafka topics
    public static void Before(Envelope envelope, ILogger logger)
    {
        logger.LogDebug("Received message from Kafka topic {TopicName} with Offset={Offset} and GroupId={GroupId}", 
            envelope.TopicName, envelope.Offset, envelope.GroupId);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L232-L244' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_kafkainstrumentation_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Connecting to Multiple Brokers <Badge type="tip" text="4.7" />

Wolverine supports interacting with multiple Kafka brokers within one application like this:

<!-- snippet: sample_using_multiple_kafka_brokers -->
<a id='snippet-sample_using_multiple_kafka_brokers'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseKafka(KafkaContainerFixture.ConnectionString);
        opts.AddNamedKafkaBroker(new BrokerName("americas"), "americas-kafka:9092");
        opts.AddNamedKafkaBroker(new BrokerName("emea"), "emea-kafka:9092");

        // Just publish all messages to Kafka topics
        // based on the message type (or message attributes)
        // This will get fancier in the near future
        opts.PublishAllMessages().ToKafkaTopicsOnNamedBroker(new BrokerName("americas"));

        // Or explicitly make subscription rules
        opts.PublishMessage<ColorMessage>()
            .ToKafkaTopicOnNamedBroker(new BrokerName("emea"), "colors");

        // Listen to topics
        opts.ListenToKafkaTopicOnNamedBroker(new BrokerName("americas"), "red");
        // Other configuration
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L168-L190' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_multiple_kafka_brokers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that the `Uri` scheme within Wolverine for any endpoints from a "named" Kafka broker is the name that you supply
for the broker. So in the example above, you might see `Uri` values for `emea://colors` or `americas://red`.

## Multi-Tenancy with a Broker per Tenant <Badge type="tip" text="6.9" />

Named brokers (above) are a *static* topology: you pin specific endpoints to a specific broker by name at
configuration time. **Broker-per-tenant** is different — it is *runtime* routing. You declare one shared topic
topology, and each tenant is served by its **own dedicated Kafka cluster**. Which cluster a message goes to (and
which cluster an inbound message came from) is decided at runtime by the message's
[tenant id](/guide/handlers/multi-tenancy), typically set through `DeliveryOptions.TenantId`:

<!-- snippet: sample_kafka_broker_per_tenant -->
<a id='snippet-sample_kafka_broker_per_tenant'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The "default" / shared Kafka cluster
        opts.UseKafka(KafkaContainerFixture.ConnectionString)
            .AutoProvision()

            // How should Wolverine route a message whose TenantId is null or
            // unknown? FallbackToDefault (the default) uses the shared cluster;
            // TenantIdRequired throws; IgnoreUnknownTenants silently drops it.
            .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

            // Each tenant gets its OWN dedicated Kafka cluster, but shares the
            // topic topology declared below. The tenant inherits the parent's
            // client configuration (auth, SASL/SSL, idempotence, DLQ topic, ...)
            // with just the bootstrap servers re-pointed.
            .AddTenant("tenant-a", "tenant-a-kafka:9092")

            // Or configure the tenant cluster through the full Kafka surface,
            // seeded from the parent settings, when it needs its own credentials:
            .AddTenant("tenant-b", tenant => tenant.ConfigureClient(client =>
            {
                client.BootstrapServers = "tenant-b-kafka:9092";
                client.SaslUsername = "tenant-b-user";
                client.SaslPassword = "tenant-b-secret";
            }));

        // One shared topology; messages are routed to the right cluster at
        // runtime by Envelope.TenantId (e.g. new DeliveryOptions { TenantId = "tenant-a" }).
        opts.PublishMessage<ColorMessage>().ToKafkaTopic("colors");
        opts.ListenToKafkaTopic("colors");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L195-L228' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_kafka_broker_per_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To route a specific message to a tenant's cluster, stamp the tenant id on the send:

```csharp
await bus.SendAsync(new ColorMessage("blue"), new DeliveryOptions { TenantId = "tenant-a" });
```

Wolverine wraps the outbound endpoint in a `TenantedSender` that dispatches on `Envelope.TenantId`, and builds a
compound listener that runs one consumer per tenant cluster — each inbound envelope is stamped with the tenant id
it was consumed under. This mirrors the [RabbitMQ](/guide/messaging/transports/rabbitmq/multi-tenancy) and
[NATS](/guide/messaging/transports/nats) broker-per-tenant support.

::: tip Named broker vs. broker-per-tenant
Use a **named broker** when a *fixed set of endpoints* should always talk to a *specific* broker. Use
**broker-per-tenant** when the *same logical endpoints* should be transparently routed to a *different cluster per
tenant* based on the runtime tenant id. They are independent features and can be combined.
:::

### Choosing the unknown-tenant behavior

`TenantIdBehavior(...)` controls what happens when a message has a null or unregistered tenant id:

* `FallbackToDefault` (the default) — route it to the shared/default cluster (the one passed to `UseKafka`).
* `TenantIdRequired` — throw; every message must carry a known tenant id.
* `IgnoreUnknownTenants` — silently drop the message.

### Consumer groups are *not* suffixed per tenant

Because each tenant is a **separate Kafka cluster**, consumer offsets live in that tenant's own cluster. Wolverine
therefore keeps the **same consumer group id** across all tenant listeners rather than suffixing it per tenant —
there is no offset collision to avoid. (This is deliberately different from transports like NATS that isolate
tenants by subject prefix on a *shared* connection.)

### Auto-provisioning per cluster

When [`AutoProvision()`](#) is enabled, Wolverine provisions the shared topic topology — including the dead letter
queue topic — on **every** tenant cluster, not just the default one, since each is an independent broker. A tenant
listener's dead-lettered messages are likewise produced to the DLQ topic on that tenant's own cluster.

::: warning Out of scope
Per-tenant [non-blocking retry topics](#non-blocking-retry-topics) and [topic replay](#replaying-a-topic) are bound
to the default cluster only; they are not currently fanned out per tenant.
:::

## Non-Blocking Retry Topics <Badge type="tip" text="6.8" />

For pure-Kafka apps that can't lean on a database, Wolverine offers **Spring/Uber-style non-blocking retry
topics**. On a matching failure the message is produced to a **tiered fixed-delay retry topic**, the source
partition's offset is committed (so the partition keeps flowing — **no head-of-line blocking**), and a
delayed consumer reprocesses the message through the normal handler pipeline once the tier delay elapses.
After the last tier is exhausted, the message lands in the existing Kafka [dead letter queue](#native-dead-letter-queue).

It's wired through the standard error-handling DSL, keyed off **exception matching** like any other policy:

```csharp
opts.OnException<TransientException>()
    .MoveToKafkaRetryTopic(1.Seconds(), 30.Seconds(), 5.Minutes());
```

Each delay defines a tier. Wolverine auto-derives one retry topic per delay, named off the source topic
(`orders.retry.1s`, `orders.retry.30s`, `orders.retry.5m`), auto-provisions them (when `AutoProvision()` is
on), and runs a delayed consumer for each. Retry/exception metadata (source topic, tier, attempt count,
first-failure time, exception) travels in headers.

::: tip Prefer the durable inbox when you have a database
This is the **DB-free** retry path. If your app uses a database, Wolverine's `ScheduleRetry(...)` (→ the
durable scheduler) is already non-blocking and is the recommended choice — it survives restarts without
extra topics. Retry topics are for pure-Kafka shops, or orgs whose tooling/observability is built around
`-retry`/`-dlt` topics.
:::

::: warning Trade-offs
- This policy **only applies to messages received over Kafka**. The same rule on a non-Kafka endpoint falls
  back to a normal inline retry (Wolverine logs a startup warning if it detects this).
- **Ordering is not preserved** for a retried flow — a message that goes to a retry topic is reprocessed
  later than messages that succeeded after it.
- The delays are **floors, not exact** — they're enforced by consumer-side waiting plus poll granularity.
- Reprocessing re-runs your handler, so make handlers **idempotent**.
:::

## Native Dead Letter Queue

Wolverine supports routing failed Kafka messages to a designated dead letter queue (DLQ) Kafka topic instead of relying on database-backed dead letter storage. This is opt-in on a per-listener basis.

Messages produced to the DLQ topic — and to the [non-blocking retry topics](#non-blocking-retry-topics) —
carry the standard Wolverine diagnostic headers as Kafka headers: `exception-type`, `exception-message`,
`exception-stack` (truncated to 8,192 characters), `failed-at`, `original-destination`, plus
`original-partition` and `original-offset` recording exactly where the failed message came from. The delivery
attempt count is on the standard `attempts` header. See
[diagnostic headers on dead letter messages](/tutorials/dead-letter-queues#diagnostic-headers-on-dead-letter-messages)
for the full cross-transport header structure.

### Enabling the Dead Letter Queue

To enable the native DLQ for a Kafka listener, use the `EnableNativeDeadLetterQueue()` method:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseKafka("localhost:9092").AutoProvision();

        opts.ListenToKafkaTopic("incoming")
            .ProcessInline()
            .EnableNativeDeadLetterQueue();
    }).StartAsync();
```

When a message fails all retry attempts, it will be produced to the DLQ Kafka topic (default: `wolverine-dead-letter-queue`) with the original message body and Wolverine envelope headers intact. The following exception metadata headers are added:

- `exception-type` - The full type name of the exception
- `exception-message` - The exception message
- `exception-stack` - The exception stack trace
- `failed-at` - Unix timestamp in milliseconds when the failure occurred

### Configuring the DLQ Topic Name

The default DLQ topic name is `wolverine-dead-letter-queue`. You can customize this at the transport level:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseKafka("localhost:9092")
            .AutoProvision()
            .DeadLetterQueueTopicName("my-app-dead-letters");

        opts.ListenToKafkaTopic("incoming")
            .ProcessInline()
            .EnableNativeDeadLetterQueue();
    }).StartAsync();
```

The DLQ topic is shared across all listeners on the same Kafka transport that have native DLQ enabled. When `AutoProvision` is enabled, the DLQ topic will be automatically created.

## Multi-Topic Listening <Badge type="tip" text="5.18" />

By default, each call to `ListenToKafkaTopic()` creates a separate Kafka consumer. If you have many topics that share
the same logical workload, this can lead to excessive consumer group rebalances and slower startup times.

Wolverine supports subscribing to multiple Kafka topics with a single consumer using `ListenToKafkaTopics()`:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseKafka("localhost:9092").AutoProvision();

        // Subscribe to multiple topics with a single consumer
        opts.ListenToKafkaTopics("orders", "invoices", "shipments")
            .ProcessInline();
    }).StartAsync();
```

This creates a single `KafkaTopicGroup` endpoint that subscribes to all listed topics using one Kafka consumer.
The endpoint name defaults to the topic names joined by underscores (e.g. `orders_invoices_shipments`), and the
URI follows the pattern `kafka://topic/orders_invoices_shipments`.

### Consumer Configuration

You can override the consumer configuration for a topic group just like for individual topics:

```csharp
opts.ListenToKafkaTopics("orders", "invoices")
    .ConfigureConsumer(config =>
    {
        config.GroupId = "order-processing";
        config.AutoOffsetReset = AutoOffsetReset.Earliest;
    });
```

### Dead Letter Queue Support

Multi-topic listeners support the same native dead letter queue as individual topic listeners:

```csharp
opts.ListenToKafkaTopics("orders", "invoices")
    .ProcessInline()
    .EnableNativeDeadLetterQueue();
```

### Topic Creation Options <Badge type="tip" text="5.27" />

You can control how Wolverine creates topics for a multi-topic listener group. The `Specification()` method lets you
set partition count, replication factor, and other topic properties uniformly or per-topic:

```csharp
// Apply the same specification to all topics in the group
opts.ListenToKafkaTopics("orders", "invoices", "shipments")
    .Specification(spec =>
    {
        spec.NumPartitions = 6;
        spec.ReplicationFactor = 3;
    });

// Or configure each topic differently by name
opts.ListenToKafkaTopics("orders", "invoices", "shipments")
    .Specification((topicName, spec) =>
    {
        spec.NumPartitions = topicName == "orders" ? 12 : 6;
    });
```

For full control over topic creation, use `TopicCreation()` which gives you direct access to Dekaf's
`IAdminClient`. Topic settings are described with Wolverine's `KafkaTopicSpecification` (`Name`,
`NumPartitions`, `ReplicationFactor` — both defaulting to `-1`, meaning the broker default — and a `Configs`
dictionary), whose `ToNewTopic()` method converts to the `Dekaf.Admin.NewTopic` the admin client consumes:

```csharp
opts.ListenToKafkaTopics("orders", "invoices")
    .TopicCreation(async (adminClient, topicName) =>
    {
        var spec = new KafkaTopicSpecification
        {
            Name = topicName,
            NumPartitions = 12,
            ReplicationFactor = 3
        };

        await adminClient.CreateTopicsAsync([spec.ToNewTopic()],
            new CreateTopicsOptions(), CancellationToken.None);
    });
```

### When to Use Multi-Topic Listening

Use `ListenToKafkaTopics()` when:

- Multiple topics feed into the same handler pipeline
- You want to reduce the number of Kafka consumer connections
- You need faster startup and fewer consumer group rebalances

Use individual `ListenToKafkaTopic()` calls when:

- Topics need different consumer configurations (e.g. different `GroupId` values)
- Topics need different processing modes (inline vs buffered vs durable)
- You want independent scaling or error handling per topic

## Externally-Owned Topics <Badge type="tip" text="6.7" />

Some topics on the Kafka cluster may be owned by an external system where your service only has consume or produce ACLs — not `CreateTopics` or `DeleteTopics`. With `AutoProvision()` enabled, Wolverine attempts to create every declared topic at startup, which fails with `Authorization failed` on topics you don't own. Likewise, `dotnet run -- resources teardown` would attempt to delete those topics.

Mark those endpoints with `ExternallyOwned()` so Wolverine leaves their lifecycle alone while still managing the topics you do own:

```csharp
opts.UseKafka("kafka.example.com:9092").AutoProvision();

// External listener — Wolverine subscribes to it, but never creates or deletes it
opts.ListenToKafkaTopic("vendor-feed-status").ExternallyOwned();

// External publisher — Wolverine produces to it, but never creates or deletes it
opts.PublishMessage<FeedAck>()
    .ToKafkaTopic("vendor-acks")
    .ExternallyOwned();

// External multi-topic group — all topics in the group are skipped
opts.ListenToKafkaTopics("vendor-a", "vendor-b").ExternallyOwned();

// Owned by us — still auto-created on startup, and torn down by `resources teardown`
opts.ListenToKafkaTopic("our-orders");
```

The flag is per-endpoint, so externally-owned and owned topics can coexist in the same `AutoProvision()` configuration. It applies symmetrically to both `SetupAsync` (startup, `resources setup`) and `TeardownAsync` (`resources teardown`).

`ExternallyOwned()` and the [Topic Creation Options](#topic-creation-options) above are the two ends of a single spectrum: use `Specification()` / `TopicCreation()` to customize how Wolverine creates topics you *do* own, and `ExternallyOwned()` to bow out entirely for topics you don't. They compose freely — you can mix all three on listeners in the same host.

::: tip
`dotnet run -- resources check` is **not** skipped for externally-owned topics. The check sends a small "ping" probe to verify each topic is reachable, which requires `Produce` access on that topic (or `KafkaUsage.ConsumeOnly` at the transport level, which skips the probe entirely). If your externally-owned topics are consume-only at the topic level but the transport publishes to other topics, prefer running `resources check` against a limited configuration, or skip it for those topics.
:::

## Disabling all Sending

Hey, you might have an application that only consumes Kafka messages, but there are a *few* diagnostics in Wolverine that
try to send messages. To completely eliminate that, you can disable all message sending in Wolverine like this:

<!-- snippet: sample_disable_all_kafka_sending -->
<a id='snippet-sample_disable_all_kafka_sending'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts
            .UseKafka(KafkaContainerFixture.ConnectionString)
            
            // Tell Wolverine that this application will never
            // produce messages to turn off any diagnostics that might
            // try to "ping" a topic and result in errors
            .ConsumeOnly();
        
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L149-L163' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_all_kafka_sending' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publisher Batching

When publishing to Kafka through the default (buffered) sender, Wolverine coalesces outgoing envelopes into batches before handing each batch to the Dekaf producer in a single `ProduceAllAsync` call. A batch is flushed when **either** of two thresholds is hit:

- the batch reaches `MessageBatchSize` envelopes (default **100**), or
- the `MessageBatchTimeout` elapses since the **most recent** envelope entered the batch (default **250 ms**).

Note that the timeout is a *debounce* — every new envelope resets the timer. A lone message
always waits the full timeout before hitting the wire, and a steady trickle of messages arriving
faster than the timeout will keep postponing the flush until `MessageBatchSize` accumulates. If
you have a low-rate stream where per-message latency matters, shrink `MessageBatchTimeout` (or
`MessageBatchSize`) rather than assuming the timeout bounds total wait.

The relevant settings on a publisher route:

```cs
opts.PublishMessage<OrderPlaced>()
    .ToKafkaTopic("orders")

    // Maximum envelopes per batch. Default 100.
    .MessageBatchSize(100)

    // Maximum time to wait for a batch to fill before flushing. Default 250ms.
    .MessageBatchTimeout(10.Milliseconds())

    // Maximum number of in-flight batches to the broker. Default 1.
    .MessageBatchMaxDegreeOfParallelism(4)

    // Bypass batching and send on the calling thread.
    .SendInline();
```

`MessageBatchSize`, `MessageBatchTimeout`, and `MessageBatchMaxDegreeOfParallelism` apply to every transport that uses Wolverine's `BatchedSender` (Kafka, Azure Service Bus, SQS/SNS, Pub/Sub, Redis, TCP, HTTP). `SendInline()` swaps the sender type entirely; when it is set on a route, the batching settings on that same route are ignored.

## Global Partitioning

Kafka topics can be used as the external transport for [global partitioned messaging](/guide/messaging/partitioning#global-partitioning). This creates a set of sharded Kafka topics with companion local queues for sequential processing across a multi-node cluster.

Use `UseShardedKafkaTopics()` within a `GlobalPartitioned()` configuration:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseKafka("localhost:9092").AutoProvision();

        opts.MessagePartitioning.ByMessage<IMyMessage>(x => x.GroupId);

        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            // Creates 4 sharded Kafka topics named "orders1" through "orders4"
            // with matching companion local queues for sequential processing
            topology.UseShardedKafkaTopics("orders", 4);
            topology.MessagesImplementing<IMyMessage>();
        });
    }).StartAsync();
```

This creates Kafka topics named `orders1` through `orders4` with companion local queues `global-orders1` through `global-orders4`. Messages are routed to the correct shard based on their group id, and Wolverine handles the coordination between nodes automatically.

## Sending Tombstone Messages <Badge type="tip" text="5.22" />

Wolverine supports sending [Kafka tombstone messages](https://medium.com/@damienthomlutz/deleting-records-in-kafka-aka-tombstones-651114655a16) — messages with a non-null key and a null value — which are used to delete records from log-compacted Kafka topics.

To send a tombstone, broadcast a `KafkaTombstone` to the target topic:

```cs
// Delete a record by key from a log-compacted topic
await bus.BroadcastToTopicAsync("my-topic", new KafkaTombstone("record-key-to-delete"));
```

When Wolverine encounters a `KafkaTombstone` message, it produces a Kafka message with the specified key and a `null` value. This signals to Kafka's log compaction process that the record with that key should be removed during the next compaction cycle.

This is useful when your Kafka topics use [log compaction](https://docs.confluent.io/platform/current/kafka/design.html#log-compaction) to maintain a key-value snapshot of the latest state. Publishing a tombstone ensures that deleted records are eventually cleaned up from the topic.

## Performance Tuning

This section collects the levers that matter most for throughput and latency with the Kafka
transport, and the factors behind them.

### Choose the endpoint mode deliberately

- **Buffered** (the default) is the fastest mode: the consume loop hands messages to an
  in-memory worker queue and keeps consuming. The trade-off is that offsets are stored as soon
  as a message is buffered, *before* it is handled — an ungraceful process crash can lose
  buffered messages (effectively at-most-once). Use it when throughput matters and your
  handlers are idempotent or losses on crash are tolerable.
- **Durable** (`UseDurableInbox()`) writes incoming messages to the database inbox before
  the offset advances. The consume loop drains up to `MaximumMessagesToReceive` (default 100)
  already-fetched records at a time and persists them with a single batched insert, so under
  load the inbox cost is paid per batch rather than per record — measured locally this took a
  2,000 msg/s stream from unbounded backlog to a steady ~32ms delivery p50 (GH-3490). Your
  database's write latency still gates the ceiling; plan for the inbox database to be close
  (latency-wise) and lightly contended, set `MaximumMessagesToReceive(1)` if you need strict
  record-at-a-time consumption, and scale with `ListenerCount` when a single listener's insert
  rate becomes the ceiling.
- **Inline** (`ProcessInline()`) processes one message at a time per consumer, end to end, and
  only then stores the offset. Strongest per-partition guarantees, lowest throughput per
  listener — scale with `ListenerCount` (bounded by the topic's partition count).

### Consumption parallelism

- `MaximumParallelMessages(n)` (`MaxDegreeOfParallelism`, default = max(processor count, 5))
  sets how many handlers run concurrently for a Buffered/Durable listener. It does nothing for
  Inline endpoints.
- `ListenerCount(n)` creates *n* independent Kafka consumers in the same group. This only helps
  if the topic has at least *n* partitions — **note that Wolverine's auto-provisioning creates
  topics with the broker default partition count (usually 1) unless you set
  `.Specification(spec => spec.NumPartitions = ...)`.** A single-partition topic cannot scale
  consumption no matter what you configure on the Wolverine side.
- For per-key ordering *with* parallelism across keys, prefer `ProcessConcurrentlyByKey()` or
  `PartitionProcessingByGroupId(...)` over hand-rolled locking in handlers — Wolverine's
  sharded execution keeps same-key messages sequential while unrelated keys proceed in
  parallel, without tying up worker slots in custom middleware waits. Custom middleware that
  blocks (semaphores, locks) both occupies a worker slot and inflates the
  `wolverine-execution-time` metric, since execution time is measured around the whole
  middleware chain.

### Offset commits

The default `CommitMode.StoreThenAutoFlush` stores offsets non-blockingly and lets the client's
auto-committer flush them in the background — keep it unless you have a specific reason.
`CommitMode.PerMessage` performs a synchronous broker round trip per message and is by far the
slowest option; use it only where its stronger guarantee is genuinely required.

### Back pressure

Buffered and Durable listeners stop receiving when more than `BufferingLimits.Maximum`
(default 1,000) messages are queued in memory, and resume below the restart threshold
(default 500). On Kafka, stopping the listener closes the consumer — which means **leaving the
consumer group and triggering a rebalance**, and another when it rejoins. If your load
regularly crosses the limit you can see a sawtooth of multi-second pauses. For sustained
high-throughput consumers, either raise `BufferingLimits` so normal bursts fit in memory, or
increase processing parallelism so the queue drains faster than it fills.

### Publishing

See [Publisher Batching](#publisher-batching) above. `MessageBatchTimeout` is the latency
floor batching adds to a route: a batch is sent when it reaches `MessageBatchSize` or when
the timeout elapses, so for low-rate streams every message waits up to the timeout (default
250ms) before hitting the wire. Latency-sensitive routes should shrink both knobs — in
GH-3490's load rig, going from `(100, 250ms)` to `(1, 1ms)` on a steady 8 msg/s stream took
publish-to-consume p50 from seconds to parity with a raw Confluent.Kafka producer (used as a
cross-library baseline), and `SendInline()` measured at native latency as well.

::: warning
On versions before the JasperFx fix for wolverine#3490, the batch timeout behaved as a
*debounce*: every published message reset the timer, so a steady stream arriving faster than
the timeout would not flush until a full batch accumulated — measured at 5.8s p50 delivery
latency for an 8 msg/s stream on the default `(100, 250ms)` settings. If you see multi-second
Kafka delivery latency on a low-rate route, tighten `MessageBatchSize`/`MessageBatchTimeout`
or upgrade.
:::

Raising `MessageBatchMaxDegreeOfParallelism` above its default of 1 allows multiple batches
in flight to the broker.

### Serialization and headers

Every message sent with the default envelope mapper carries the full set of Wolverine envelope
headers (roughly 18 headers, written and parsed per message). For very high-volume topics
where you control both ends — or interop scenarios — a raw JSON mapper (`ReceiveRawJson()` /
`PublishRawJson()`) or a custom `IKafkaEnvelopeMapper` (mapping Dekaf's `ConsumeResult<string, byte[]>` in
and Wolverine's `OutgoingKafkaRecord` out) that carries only what you need reduces per-message overhead and
payload size.

### Interpreting Wolverine's metrics

When comparing Wolverine's numbers to your own measurements, note the interval definitions:
`wolverine-execution-time` measures the handler *and all middleware* (including any time
blocked inside middleware), while `wolverine-effective-time` is wall-clock from the producer's
`SentAt` stamp through handling, cascading message flush, *and* the final acknowledgement —
and it is sensitive to clock skew between producing and consuming machines.

## URI reference

The `KafkaEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `kafka://topic/{name}` | `KafkaEndpointUri.Topic("name")` |

```csharp
using Wolverine.Kafka;

var uri = KafkaEndpointUri.Topic("orders");
```
