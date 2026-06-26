# Using Kafka

::: warning
The Kafka transport does not really support the "Requeue" error handling policy in Wolverine. "Requeue" in this case becomes
effectively an inline "Retry"
:::

## Installing

To use [Kafka](https://www.confluent.io/what-is-apache-kafka/) as a messaging transport with Wolverine, first install the `WolverineFx.Kafka` library via nuget to your project. Behind the scenes, this package uses the [Confluent.Kafka client library](https://github.com/confluentinc/confluent-kafka-dotnet) managed library for accessing Kafka brokers.

```bash
dotnet add package WolverineFx.Kafka
```

## Aspire Integration

::: tip
See the full [Aspire + Wolverine Kafka sample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/AspireWithKafka) for a working end-to-end example.
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
    configureProducers: p => p.MessageMaxBytes = 1_000_000)
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L14-L133' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_kafka' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The various `Configure*****()` methods provide quick access to the full API of the Confluent Kafka library for security
and fine tuning the Kafka topic behavior. 

## Listener Consumer Settings <Badge type="tip" text="5.16" />

When building a Kafka listener, Wolverine configures the underlying Confluent Kafka `ConsumerConfig` differently
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
locally (`EnableAutoOffsetStore = false` + `StoreOffset`) and Kafka's background committer flushes them on
`AutoCommitIntervalMs`. There is **no** synchronous broker round trip per message.

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

Two consumer settings make that native assignment stable and production-grade. Both are **opt-in** —
Wolverine does not change the defaults, because silently switching an existing group's assignment strategy
breaks live rolling upgrades.

```csharp
opts.UseKafka(connectionString)
    // Incremental rebalancing: a rebalance keeps each consumer's unaffected partitions instead of a
    // stop-the-world revoke-everything cycle.
    .UseCooperativeStickyAssignment()

    // Static membership: rolling restarts/deploys of the same node don't trigger partition churn.
    // The group.instance.id defaults to POD_NAME, then HOSTNAME, then the machine name.
    .UseStaticMembership();
```

Both are also available per-listener on `ListenToKafkaTopic(...)` (`UseCooperativeStickyAssignment()` /
`UseStaticMembership(...)`).

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

::: tip Rolling-upgrade path onto cooperative-sticky
Don't flip an existing, running group straight from the default (eager) assignor to cooperative-sticky — a
group must not mix eager and cooperative members. Do a two-step deploy: first roll out a build that lists
**both** strategies (`[CooperativeSticky, Range]`) so every member supports cooperative, then a second
deploy that drops the eager strategy.
:::

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
owns both, so a crash or rebalance can't lose A. Pairs naturally with cooperative-sticky (above), which
keeps a rebalance from disrupting unaffected partitions.

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
topic back through the **normal handler pipeline**. It uses a throwaway `Assign()`-based consumer with a
unique group id and **never commits to the live consumer group**, so steady-state consumption is
completely untouched.

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

### Idempotent producer

Opt into the idempotent producer so producer-side retries can't write duplicates to the broker:

```csharp
opts.UseKafka(connectionString).UseIdempotentProducer();       // node-wide
opts.PublishMessage<T>().ToKafkaTopic("t").UseIdempotentProducer();  // per topic
```

This sets `enable.idempotence = true` (which implies `acks=all` and bounded in-flight requests). It is
**producer→broker** de-duplication only — it does not make consume-process-produce atomic, and it has a
slight throughput cost. Opt-in; the default is unchanged.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/when_publishing_and_receiving_by_partition_key.cs#L15-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_kafka_by_partition_key' title='Start of snippet'>anchor</a></sup>
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
that aren't also Wolverine applications. At this time, it's possible to send or receive raw JSON through Kafka and Wolverine
by using the options shown below in test harness code:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/publish_and_receive_raw_json.cs#L21-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_raw_json_sending_and_receiving_with_kafka' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Confluent Schema Registry Serializers <Badge type="tip" text="5.27" />

When you need to interoperate with other Kafka clients that use the [Confluent Schema Registry](https://docs.confluent.io/platform/current/schema-registry/index.html)
wire format, Wolverine provides built-in serializers for both **JSON Schema** and **Avro** that integrate directly with
the Schema Registry. These serializers handle schema registration, evolution, and the Confluent wire format
(magic byte + 4-byte schema ID prefix) automatically.

### JSON Schema Serializer

The `SchemaRegistryJsonSerializer` works with any .NET class — no code generation or special interfaces required:

```csharp
using Confluent.SchemaRegistry;
using Wolverine.Kafka.Serialization;

var schemaRegistry = new CachedSchemaRegistryClient(
    new SchemaRegistryConfig { Url = "http://localhost:8081" });

opts.PublishMessage<OrderPlaced>()
    .ToKafkaTopic("orders")
    .DefaultSerializer(new SchemaRegistryJsonSerializer(schemaRegistry));

opts.ListenToKafkaTopic("orders")
    .DefaultSerializer(new SchemaRegistryJsonSerializer(schemaRegistry));
```

You can also pass a `JsonSerializerConfig` to control schema auto-registration and compatibility settings:

```csharp
var serializer = new SchemaRegistryJsonSerializer(schemaRegistry,
    new JsonSerializerConfig
    {
        AutoRegisterSchemas = true,
        SubjectNameStrategy = SubjectNameStrategy.TopicRecord
    });
```

### Avro Serializer

The `SchemaRegistryAvroSerializer` works with Avro-generated classes that implement `Avro.Specific.ISpecificRecord`.
Use the [Apache Avro tools](https://avro.apache.org/docs/current/getting-started-csharp/) or the
[Confluent avrogen tool](https://docs.confluent.io/platform/current/schema-registry/serdes-develop/serdes-avro.html)
to generate C# classes from `.avsc` schema files:

```csharp
using Confluent.SchemaRegistry;
using Wolverine.Kafka.Serialization;

var schemaRegistry = new CachedSchemaRegistryClient(
    new SchemaRegistryConfig { Url = "http://localhost:8081" });

opts.PublishMessage<OrderPlacedAvro>()
    .ToKafkaTopic("orders-avro")
    .DefaultSerializer(new SchemaRegistryAvroSerializer(schemaRegistry));

opts.ListenToKafkaTopic("orders-avro")
    .DefaultSerializer(new SchemaRegistryAvroSerializer(schemaRegistry));
```

### How It Works

Both serializers extend the `SchemaRegistrySerializer` base class which implements Wolverine's `IMessageSerializer`
interface. Internally, each serializer:

1. Creates Confluent `IAsyncSerializer<T>` / `IAsyncDeserializer<T>` instances per message type on first use
2. Caches these typed serializer delegates in a `ConcurrentDictionary` for subsequent calls
3. Delegates all schema negotiation to the Confluent Schema Registry client library

The serialized bytes use the standard [Confluent wire format](https://docs.confluent.io/platform/current/schema-registry/fundamentals/serdes-develop/index.html#wire-format):
a magic byte (`0x00`), followed by a 4-byte big-endian schema ID, followed by the payload. This makes your
Wolverine producers and consumers fully compatible with any other Kafka client that uses the Schema Registry
(Java, Python, Go, etc.).

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L183-L195' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_kafkainstrumentation_middleware' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L157-L179' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_multiple_kafka_brokers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that the `Uri` scheme within Wolverine for any endpoints from a "named" Kafka broker is the name that you supply
for the broker. So in the example above, you might see `Uri` values for `emea://colors` or `americas://red`.

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

For full control over topic creation, use `TopicCreation()` which gives you direct access to the Kafka `IAdminClient`:

```csharp
opts.ListenToKafkaTopics("orders", "invoices")
    .TopicCreation(async (adminClient, topicName) =>
    {
        var spec = new TopicSpecification
        {
            Name = topicName,
            NumPartitions = 12,
            ReplicationFactor = 3
        };

        await adminClient.CreateTopicsAsync(new[] { spec });
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L138-L152' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_all_kafka_sending' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publisher Batching

When publishing to Kafka through the default (buffered) sender, Wolverine coalesces outgoing envelopes into batches before handing them to the Kafka producer. A batch is flushed when **either** of two thresholds is hit:

- the batch reaches `MessageBatchSize` envelopes (default **100**), or
- the `MessageBatchTimeout` elapses since the first envelope entered the batch (default **250 ms**).

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

## URI reference

The `KafkaEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `kafka://topic/{name}` | `KafkaEndpointUri.Topic("name")` |

```csharp
using Wolverine.Kafka;

var uri = KafkaEndpointUri.Topic("orders");
```
