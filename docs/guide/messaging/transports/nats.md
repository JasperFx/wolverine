# Using NATS

::: tip
Wolverine uses the official [NATS.Net client](https://github.com/nats-io/nats.net) to connect to NATS.
:::

## Installing

To use [NATS](https://nats.io/) as a messaging transport with Wolverine, first install the `WolverineFx.Nats` library via NuGet:

```bash
dotnet add package WolverineFx.Nats
```

## Core NATS vs JetStream

NATS provides two distinct messaging models:

| Feature | Core NATS | JetStream |
|---------|-----------|-----------|
| **Persistence** | None (memory only) | Configurable (memory/file) |
| **Delivery Guarantee** | At-most-once | At-least-once |
| **Acknowledgments** | None | Full support (ack/nak/term) |
| **Requeue** | Via republish | Native via `NakAsync()` |
| **Dead Letter** | Not available | Via `AckTerminateAsync()` |
| **Scheduled Delivery** | Not available | Native (Server 2.12+) |

Choose **Core NATS** for:
- Real-time notifications where message loss is acceptable
- Low-latency fire-and-forget messaging
- Heartbeats and ephemeral events

Choose **JetStream** for:
- Commands and events requiring durability
- Workflows where message delivery must be guaranteed
- Scenarios requiring replay or scheduled delivery

## Basic Configuration

### Core NATS (Simple Pub/Sub)

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Connect to NATS
        opts.UseNats("nats://localhost:4222")
            .AutoProvision();

        // Listen to a subject
        opts.ListenToNatsSubject("orders.received")
            .ProcessInline();

        // Publish to a subject
        opts.PublishAllMessages()
            .ToNatsSubject("orders.received");
    }).StartAsync();
```

### JetStream (Durable Messaging)

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseNats("nats://localhost:4222")
            .AutoProvision()
            .UseJetStream(js => { })
            .DefineWorkQueueStream("ORDERS", "orders.>");

        // Listen with JetStream consumer
        opts.ListenToNatsSubject("orders.received")
            .UseJetStream("ORDERS", "orders-consumer");

        // Publishing automatically uses JetStream when stream is defined
        opts.PublishAllMessages()
            .ToNatsSubject("orders.received");
    }).StartAsync();
```

## Connecting with Aspire or a Named Connection String <Badge type="tip" text="6.52" />

Let's say you're running your system under [.NET Aspire](/guide/aspire), and your AppHost declares a NATS server and
references it from your Wolverine service:

```csharp
// In the AppHost
var nats = builder.AddNats("nats").WithJetStream();

builder.AddProject<Projects.MyService>("service")
    .WithReference(nats)
    .WaitFor(nats);
```

Aspire hands your service a `nats://` URL at `ConnectionStrings:nats`, and all you need to tell Wolverine is that name:

<!-- snippet: sample_nats_using_named_connection -->
<a id='snippet-sample_nats_using_named_connection'></a>
```cs
// "nats" is the name of the NATS resource in the Aspire AppHost,
// i.e. builder.AddNats("nats"), referenced by this project
opts.UseNatsUsingNamedConnection("nats");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/NATS/Wolverine.Nats.Tests/named_connection_5126.cs#L77-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_nats_using_named_connection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The value can carry credentials in the form `nats://user:password@host:port`. `UseNatsUsingNamedConnection()` also
takes an optional `Action<NatsTransportConfiguration>` for authentication, TLS, or JetStream settings. Just know that
the URL from the named connection string is applied after that action, so it wins over any `ConnectionString` you set
in there.

If your application talks to more than one NATS server, there's a named broker version too:

<!-- snippet: sample_nats_named_broker_using_named_connection -->
<a id='snippet-sample_nats_named_broker_using_named_connection'></a>
```cs
opts.UseNatsUsingNamedConnection("nats");

// A second, independent NATS broker whose URL comes from
// builder.AddNats("nats-audit") in the Aspire AppHost
opts.AddNamedNatsBrokerUsingNamedConnection(new BrokerName("audit"), "nats-audit");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/NATS/Wolverine.Nats.Tests/named_connection_5126.cs#L122-L130' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_nats_named_broker_using_named_connection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Aspire Integration](/guide/aspire) for the full picture, including what happens when a connection string is
missing.

### Reading the Connection String Yourself

If you'd rather read the connection string that Aspire injects yourself through `IConfiguration.GetConnectionString()`, that works too. Aspire injects the NATS URL when you use `.WithReference()` in the AppHost.

**AppHost** (`Aspire.Hosting.Nats` NuGet):
```csharp
var nats = builder.AddNats("nats")
    .WithJetStream();

builder.AddProject<Projects.MyWorker>("worker")
    .WithReference(nats)
    .WaitFor(nats);
```

**Service project:**
```csharp
var builder = Host.CreateApplicationBuilder(args);

// Aspire injects ConnectionStrings__nats as a nats:// URL automatically via WithReference()
var natsUrl = builder.Configuration.GetConnectionString("nats")
    ?? "nats://localhost:4222";

builder.UseWolverine(opts =>
{
    opts.UseNats(natsUrl)
        .AutoProvision();

    opts.ListenToNatsSubject("orders").UseJetStream("ORDERS", "orders-consumer");
    opts.PublishMessage<OrderPlaced>().ToNatsSubject("orders");
});

await builder.Build().RunAsync();
```

`WaitFor(nats)` in the AppHost ensures NATS is healthy before your service starts, making `AutoProvision()` reliable.

## Connection Configuration

### Basic Connection

```csharp
opts.UseNats("nats://localhost:4222");
```

### Connection with Timeouts

```csharp
opts.UseNats("nats://localhost:4222")
    .ConfigureTimeouts(
        connectTimeout: TimeSpan.FromSeconds(10),
        requestTimeout: TimeSpan.FromSeconds(30)
    );
```

### Customizing the NATS Client Options <Badge type="tip" text="6.47" />

For NATS.Net client settings the transport does not surface itself, `ConfigureNatsOpts()` hands you the
`NatsOpts` Wolverine built for a connection and opens the connection with whatever you return. It runs last,
after Wolverine has applied its own configuration and named the connection, and it applies to the shared
connection and to every tenant's [dedicated connection](#per-tenant-connections):

```csharp
opts.UseNats("nats://localhost:4222")
    .ConfigureNatsOpts(o => o with
    {
        // Core NATS subscriptions buffer up to SubPendingChannelCapacity messages per subscription
        // and, by default, drop the newest ones once that buffer is full
        SubPendingChannelCapacity = 4096,

        // Notice a dead connection sooner than the client default of 2 minutes x 2 missed pings
        PingInterval = TimeSpan.FromSeconds(10)
    });
```

Without the hook the NATS.Net defaults apply unchanged: a pending channel of 1,024 messages per subscription
that drops the newest message when full (`BoundedChannelFullMode.DropNewest`), and a ping every two minutes with
two outstanding pings allowed. Think twice before switching `SubPendingChannelFullMode` to `Wait`: a full channel
then stalls the connection's read loop instead of dropping, so one slow core NATS listener holds up every
subscription on that connection, and the server eventually disconnects the client as a slow consumer.

Wolverine hands NATS.Net the host's `ILoggerFactory`, so the client's own messages — connection changes, and
errors the server reports only to the client, such as a permissions violation — appear in the application log
under the `NATS.Client` categories. Raise their minimum level there to quiet them, or replace `LoggerFactory` in
the hook.

A tenant added with its own connection configuration can set `ConfigureNatsOpts` on that configuration, which
replaces the transport's hook for that tenant's connection:

```csharp
opts.UseNats("nats://shared:4222")
    .AddTenant("tenant-a", cfg =>
    {
        cfg.ConnectionString = "nats://tenant-a-host:4222";
        cfg.ConfigureNatsOpts = o => o with { SubPendingChannelCapacity = 10_000 };
    });
```

### Dropped Core NATS Messages <Badge type="tip" text="6.47" />

A core NATS subscription buffers incoming messages in a pending channel of 1,024 messages, and NATS.Net drops the
newest message once that channel is full. Core NATS never redelivers it. Wolverine logs a warning from
`NatsTransport` for every dropped message (subject, subscription, connection) and one for each slow-consumer
episode of a subscription, on the shared and on the tenant connections. If you see them, raise
`SubPendingChannelCapacity` through `ConfigureNatsOpts()`, scale out the listener, or move the subject to
JetStream.

## Authentication

### Username and Password

```csharp
opts.UseNats("nats://localhost:4222")
    .WithCredentials("username", "password");
```

### Token Authentication

```csharp
opts.UseNats("nats://localhost:4222")
    .WithToken("my-secret-token");
```

### NKey Authentication

```csharp
opts.UseNats("nats://localhost:4222")
    .WithNKey("/path/to/nkey.file");
```

### TLS Configuration

```csharp
opts.UseNats("nats://localhost:4222")
    .UseTls(insecureSkipVerify: false);
```

## JetStream Configuration

### Configuring JetStream Defaults

```csharp
opts.UseNats("nats://localhost:4222")
    .UseJetStream(js =>
    {
        js.MaxDeliver = 5;           // Max redelivery attempts
        js.AckWait = TimeSpan.FromSeconds(30);
        js.DuplicateWindow = TimeSpan.FromMinutes(2);
    });
```

`MaxDeliver`, `AckWait` and `DeliverPolicy` apply to the consumers of Wolverine's JetStream listeners.
`MaxDeliver` is also the only limit on a message whose dead-letter copy keeps failing on an Inline or native-ack
listener: it comes back every five seconds until the consumer has delivered it `MaxDeliver` times, and forever with
`MaxDeliver = -1` — see "When the dead-letter copy fails" under [Error Handling](#error-handling). The stream
limits in `JetStreamDefaults` (`MaxAge`, `MaxMessages`, `MaxBytes`, `Replicas`) only apply to a stream that
`resources setup` / `AddResourceSetupOnStartup()` creates because a JetStream endpoint's stream does not exist yet. A stream
you declare with `DefineStream()` and friends does not inherit them — whatever its declaration leaves unset is
unlimited — and only `DuplicateWindow` serves as its default.

### Consumer Deliver Policy

When Wolverine auto-provisions a JetStream consumer for a listener it leaves the consumer config's `DeliverPolicy` unset, which falls through to NATS's own default of `DeliverPolicy.All` — every message currently in the stream is replayed when the consumer first connects. For new listeners attached to a long-running stream that's usually not what you want.

Set a transport-wide default through `JetStreamDefaults.DeliverPolicy` so every auto-provisioned consumer under this transport starts at the same position:

```csharp
opts.UseNats("nats://localhost:4222")
    .UseJetStream(js =>
    {
        js.DeliverPolicy = ConsumerConfigDeliverPolicy.New; // only messages
                                                            // from now on
    });
```

Override per-listener with `DeliverFrom(...)` when a single endpoint needs a different position:

```csharp
opts.ListenToNatsSubject("orders.received")
    .UseJetStream("ORDERS")
    .DeliverFrom(ConsumerConfigDeliverPolicy.New);
```

The per-listener override always wins over the transport-wide default. When neither is set Wolverine writes nothing to the consumer config and the NATS server default (`All`) applies.

A stream with work-queue retention refuses any consumer that does not deliver all (`consumer must be deliver all
on workqueue stream`, err 10101). The shard listeners of [`UseShardedNatsSubjects()`](#global-partitioning)
therefore always deliver all on the work-queue streams Wolverine declares for them, whatever the transport-wide
default says. On a work-queue stream you declare yourself, give the listeners
`DeliverFrom(ConsumerConfigDeliverPolicy.All)` when you set a transport-wide default. The same goes for the stream
resource setup creates for a JetStream endpoint whose stream is not declared, which gets work-queue retention.

The override only applies to consumers Wolverine itself auto-provisions. If you reference a pre-created consumer by name with `UseJetStream(streamName, consumerName)`, Wolverine keeps that consumer's existing `DeliverPolicy` regardless of `DeliverFrom(...)` — JetStream does not allow changing it on an existing consumer, so pre-creating the consumer with the desired policy via the NATS CLI or `JetStream` API is the right tool there.

| `ConsumerConfigDeliverPolicy` | Effect |
|---|---|
| `All` | Replay every message currently in the stream (NATS-server default). |
| `New` | Only deliver messages that arrive **after** the consumer is created. |
| `Last` | Deliver only the latest message in the stream. |
| `LastPerSubject` | Deliver the latest message per matching subject filter. |
| `ByStartSequence` / `ByStartTime` | Start from a specific sequence number or timestamp. Requires pre-creating the consumer outside Wolverine — `OptStartSeq` / `OptStartTime` have no listener-configuration surface. |

### Defining Streams

#### Work Queue Stream (Retention by Interest)

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineWorkQueueStream("ORDERS", "orders.>");
```

::: warning Interest retention, not JetStream work-queue retention
Despite their names, `DefineWorkQueueStream()` and `StreamConfiguration.AsWorkQueue()` set
`StreamConfigRetention.Interest`. An interest stream keeps a message only while a consumer is interested in it,
so a message published while no consumer is bound — before the first listener starts, or between two
deployments — is discarded on arrival. For JetStream's work-queue retention, which keeps every message until a
consumer acknowledges it, set the retention explicitly:

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineStream("ORDERS", s =>
    {
        s.WithSubjects("orders.>");
        s.Retention = StreamConfigRetention.Workqueue;
    });
```
:::

#### Work Queue with Additional Configuration

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineWorkQueueStream("ORDERS", 
        stream => stream.WithLimits(maxAge: TimeSpan.FromDays(1)), 
        "orders.>");
```

#### Custom Stream Configuration

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineStream("EVENTS", stream =>
    {
        stream.WithSubjects("events.>")
              .WithLimits(maxMessages: 1_000_000, maxAge: TimeSpan.FromDays(7))
              .WithReplicas(3)
              .EnableScheduledDelivery();
    });
```

#### Log Stream (Time-Based Retention)

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineLogStream("LOGS", TimeSpan.FromDays(30), "logs.>");
```

#### Replicated Stream (High Availability)

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineReplicatedStream("CRITICAL", replicas: 3, "critical.>");
```

#### Mirrors, Sources and Placement <Badge type="tip" text="6.49" />

A declared stream can be a mirror of another stream, aggregate messages from other streams, or carry a
placement for the server to honour. All three take the NATS.Net model types directly:

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineStream("ORDERS", s => s.WithSubjects("orders.>"))

    // A mirror has no subjects of its own; it replicates its origin
    .DefineStream("ORDERS_MIRROR", s => s.MirrorOf("ORDERS", m => m.FilterSubject = "orders.eu.>"))

    // An aggregate keeps its own subjects and pulls from other streams as well
    .DefineStream("AUDIT", s => s
        .WithSubjects("audit.>")
        .SourcedFrom("ORDERS")
        .SourcedFrom("PAYMENTS", source => source.OptStartSeq = 1000))

    // Placement: a cluster name, tags the server must carry, or both
    .DefineStream("CRITICAL", s => s.WithSubjects("critical.>").WithReplicas(3).PlacedOn("east", "ssd"));
```

They are managed the way the other stream settings are, with one difference: only when declared. A stream
whose mirror, sources or placement an operator set by hand keeps them under `CreateOrUpdate` unless the
configuration names its own, and `Provisioning(Verify)` compares them only when the configuration declares
them.

### JetStream Domain

For multi-tenant or leaf node configurations:

```csharp
opts.UseNats("nats://localhost:4222")
    .UseJetStreamDomain("my-domain");
```

The domain applies to every JetStream call the transport makes: publishing, listening, auto-provisioning, and
the stream and consumer setup done by `resources setup` / `AddResourceSetupOnStartup()`
<Badge type="tip" text="6.47" />. Resource setup only creates a stream when the lookup reports it as missing;
any other failure, such as no JetStream answering for the domain, fails the setup instead.

## Listening to Messages

### Inline Processing

Messages are processed immediately on the NATS subscription thread:

```csharp
opts.ListenToNatsSubject("orders.received")
    .ProcessInline();
```

### Buffered Processing

Messages are queued in memory and processed by worker threads:

```csharp
opts.ListenToNatsSubject("orders.received")
    .BufferedInMemory();
```

### JetStream Consumer

```csharp
opts.ListenToNatsSubject("orders.received")
    .UseJetStream("ORDERS", "my-consumer");
```

Without a consumer name, as for the shard listeners of `UseShardedNatsSubjects()`, Wolverine creates an ephemeral
consumer named after the listener's subject: the subject itself when it has a single token, otherwise the subject
with `.` turned into `_` and a short hash appended (`orders_received_1a2b3c4d`). The server removes an ephemeral
consumer shortly after its listener stops (by default after 5 seconds); name the consumer to keep it, and its
position, across restarts. The derived name is the same on every node and in every application that listens to
that subject on that stream without a consumer name, so they share one consumer and split its messages between
them; give each application a consumer name of its own when every one of them should receive all messages.

A durable inbox or outbox needs JetStream: `UseDurableInbox()` and `UseDurableOutbox()` are only allowed on a
JetStream endpoint. The policies `UseDurableInboxOnAllListeners()` and `UseDurableOutboxOnAllSendingEndpoints()`
make every JetStream listener and publishing endpoint durable and leave core NATS endpoints as they are. A durable
JetStream listener settles failures through the durable inbox: a message moved to the error queue goes to the
message store's dead letters, not to the listener's `DeadLetterTo(...)` subject.

### Native Acks with Parallel Processing <Badge type="tip" text="6.30" />

JetStream listeners support `EndpointMode.NativeAck` (see [GH-3708](https://github.com/JasperFx/wolverine/issues/3708)),
which combines `BufferedInMemory`'s parallelism and group partitioning with `ProcessInline()`'s no-loss behavior, and
needs no database at all:

```csharp
opts.ListenToNatsSubject("webhooks")
    .UseJetStream("WEBHOOKS", "webhook-processors")
    .ProcessInParallelWithNativeAcks()
    .PartitionProcessingByGroupId(PartitionSlots.Seven);
```

Incoming messages flow through an in-memory (optionally group-partitioned) execution block while the JetStream
delivery is held **unacknowledged**, and are settled natively — `Ack` on handler success, `Nak` or `AckTerminate`
on terminal failure — from the completion continuation. JetStream qualifies for this mode because each delivery is
settled individually against the message's own reply subject, so one delivery can be settled, out of order, from
whichever worker thread finished it.

The guarantee is the one the mode promises everywhere: **messages sharing a group id never execute concurrently**,
and nothing is acknowledged until its handler succeeds, so a node that dies mid-flight has every unfinished message
redelivered. Processing in original stream order is *best effort*, not promised — a failed or redelivered message
re-enters its lane later, never concurrently. Use `UseDurableInbox()` if you need strict ordering under failure.

::: warning Core NATS cannot use this mode
`ProcessInParallelWithNativeAcks()` requires JetStream and is **refused at bootstrap** on a core NATS subject. Core
NATS delivers a message once and forgets it — there is no unacknowledged delivery to hold and no redelivery when a
node dies — so the crash-safety story the mode exists for does not exist there. Use `BufferedInMemory()` or
`ProcessInline()` on core NATS.
:::

#### AckWait is a real clock, and Wolverine renews it

Unlike RabbitMQ, where an unacknowledged delivery lives until the channel closes, JetStream redelivers anything
still unacknowledged after the consumer's `AckWait`. Under native acks a message is held unsettled for **lane queue
time plus handler time**, and lane queue time is unbounded by design — so Wolverine keeps the lease alive by sending
JetStream's `AckProgress` for every queued-but-unsettled message, every half `AckWait`, for as long as it sits in a
lane ([GH-4048](https://github.com/JasperFx/wolverine/issues/4048)). You do not configure this; it is what the mode
requires to be correct on this transport.

Each renewal is sent as a **double ack** (a request the server answers) rather than fire-and-forget, because that
is the only way a lost lease — a deleted consumer, a dead connection — can be detected at all. If a lease is lost
before a message starts executing, it is dropped from its lane without being settled, so the server's redelivery is
the only remaining copy rather than a second one.

Two knobs, both optional:

```csharp
opts.ListenToNatsSubject("webhooks")
    .UseJetStream("WEBHOOKS", "webhook-processors")
    .ProcessInParallelWithNativeAcks()

    // How long the server waits before redelivering an unacked message, and therefore how
    // often Wolverine renews. Default 30 seconds (JetStreamDefaults.AckWait).
    .AckWait(TimeSpan.FromSeconds(30))

    // Ceiling on how long ONE delivery may be kept alive by renewals, measured from receipt.
    // Past this Wolverine stops renewing, so a wedged handler cannot pin a MaxAckPending slot
    // forever. Default 12 hours.
    .MaximumAckExtension(TimeSpan.FromHours(1));
```

#### MaxAckPending is the prefetch, and it must cover every lane

`MaxAckPending` — the number of deliveries JetStream will leave unacknowledged before it stops delivering — is this
transport's prefetch equivalent, and in this mode it *is* the back pressure. Wolverine defaults it to **twice the
number of lanes that can be busy at once**: the `PartitionProcessingByGroupId()` slot count when the endpoint is
partitioned, otherwise `MaximumParallelMessages()`. Every other endpoint mode leaves the NATS server default of
1,000 alone.

Sizing it below the lane count makes the consumer **stall itself** — the server stops delivering while lanes sit
idle waiting for work. Override it only if you know why:

```csharp
opts.ListenToNatsSubject("webhooks")
    .UseJetStream("WEBHOOKS", "webhook-processors")
    .ProcessInParallelWithNativeAcks()
    .MaxAckPending(64);
```

::: warning Don't call `SendInline()` on a subject you also listen to
A publisher and a listener for the same subject resolve to the **same** endpoint, and `EndpointMode` is a single
property on it — so `opts.PublishMessage<T>().ToNatsSubject("x").SendInline()` sets `Mode = Inline` and silently
downgrades a `ProcessInParallelWithNativeAcks()` listener on `"x"` out of native acks. It is also unnecessary: a
native-ack endpoint already sends through the inline sending agent.
:::

::: tip Server-side dedup is a different guarantee
JetStream's `Nats-Msg-Id` duplicate window (see [JetStream Configuration](#jetstream-configuration)) deduplicates on
**publish**, at stream ingest. It does not deduplicate *redeliveries* — a redelivered message was already accepted
into the stream, so the window never sees it again. Native acks are at-least-once by design and produce a burst of
redeliveries on every rolling deploy, so if you want those suppressed, that is what `WithInMemoryIdempotency()` is
for. The two are complementary, not alternatives.
:::

### Named Endpoints

```csharp
opts.ListenToNatsSubject("orders.received")
    .Named("orders-listener");
```

### Load Balancing with Queue Groups

Multiple listeners sharing a NATS queue group have each message delivered to only one member, spreading load
across instances. Set a transport-wide default so every listener joins the same group:

```csharp
opts.UseNats(nats =>
{
    nats.ConnectionString = "nats://localhost:4222";
    nats.DefaultQueueGroup = "orders-workers";
});
```

::: tip Subject normalization
By default the transport normalizes `/` separators in subjects to NATS `.` tokens (`NormalizeSubjects`, on by
default). Set `nats.NormalizeSubjects = false` if you need to use literal subjects that contain `/`.
:::

## Publishing Messages

### To a Specific Subject

```csharp
opts.PublishMessage<OrderCreated>()
    .ToNatsSubject("orders.created");
```

### All Messages to a Subject

```csharp
opts.PublishAllMessages()
    .ToNatsSubject("events");
```

### Inline Sending

Send messages synchronously without buffering:

```csharp
opts.PublishAllMessages()
    .ToNatsSubject("orders")
    .SendInline();
```

### Static Outgoing Headers

Attach a constant header to every message published to a subject with `AddOutgoingHeader`:

```csharp
opts.PublishMessage<OrderCreated>()
    .ToNatsSubject("orders.created")
    .AddOutgoingHeader("x-source", "orders-service");
```

### Per-Message (Dynamic) Subjects

`ToNatsSubject("...")` publishes to a single static subject. To compute the subject *per message* — e.g. an
aggregate-scoped subject like `orders.events.{id}` — use `PublishMessagesToNatsSubject<T>`. This is built on
Wolverine's generic topic routing (`RoutingMode.ByTopic` / `Envelope.TopicName`), the same mechanism the
RabbitMQ, Kafka, and MQTT transports use, so it also participates in `IMessageBus.BroadcastToTopicAsync`.

```csharp
opts.UseNats("nats://localhost:4222").AutoProvision();

// The subject is derived from each message instance.
opts.PublishMessagesToNatsSubject<OrderEvent>(e => $"orders.events.{e.OrderId}");
```

The same endpoint is automatically enrolled for explicit topic broadcasts, where the caller supplies the
subject directly (overriding the function):

```csharp
await bus.BroadcastToTopicAsync("orders.events.12345", new OrderShipped(...));
```

::: tip Consuming dynamic subjects
Because the publish subject varies, a consumer must subscribe to the whole space with a NATS wildcard.
For Core NATS, listen on `orders.events.>`. For JetStream, provision the stream over a wildcard subject
(`orders.events.>`) so it captures every computed subject, then listen with a matching consumer filter. A
too-narrow stream subject silently fails to capture the dynamic subjects.
:::

For subject shaping that a strongly-typed `Func<T, string>` can't express — for example deriving the subject
from an envelope header or tenant id — configure an `ISubjectResolver`. It runs after the base/topic subject
is determined and can rewrite it from any envelope state:

```csharp
opts.UseNats(nats =>
{
    nats.ConnectionString = "nats://localhost:4222";
    nats.SubjectResolver = new MyAggregateSubjectResolver();
});
```

### Publishing to a NATS Subject and to a Local Queue at the Same Time <Badge type="tip" text="6.49" />

Let's say you want every `DynamicLocalCommand` to go out to a NATS subject computed from the message, but you
*also* want the same message handled right here in this application through a local queue. You might reach for
both `PublishMessagesToNatsSubject<T>()` and an explicit `PublishMessage<T>().ToLocalQueue()` rule, but with the
original overload the NATS subject wins and the local queue rule is never even consulted. To make the NATS
routing *additive* so that Wolverine's other routing rules still apply, pass `additive: true`:

<!-- snippet: sample_publish_to_nats_subject_and_local_queue -->
<a id='snippet-sample_publish_to_nats_subject_and_local_queue'></a>
```cs
opts.UseNats(natsUrl).AutoProvision();

// Every DynamicLocalCommand goes to a NATS subject computed from the message...
opts.PublishMessagesToNatsSubject<DynamicLocalCommand>(m => $"commands.{m.ListenerName}", additive: true);

// ...and because that routing is additive, to the local "commands" queue at the same time
opts.PublishMessage<DynamicLocalCommand>().ToLocalQueue("commands");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/NATS/Wolverine.Nats.Tests/dynamic_subject_combined_with_local_route.cs#L44-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_nats_subject_and_local_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With that configuration, a single `PublishAsync()` sends the message to `commands.{ListenerName}` over NATS and
also drops it into the local `commands` queue. You don't even need the explicit local queue rule if all you want
is for a handler in this application to get the message, because the conventional local routing to that handler
applies as well -- just like it would if the NATS routing weren't there at all.

::: tip
The original `PublishMessagesToNatsSubject<T>(Func<T, string>)` overload is unchanged, and the NATS subject is
the only route that explicit publishing rules or local routing will leave those messages. If you're ever unsure
what Wolverine is going to do with a message type, `IWolverineRuntime.ExplainRoutingFor(type)` will walk you
through every routing rule it consulted. The additive NATS routing shows up there as `[additive, pass-through]`.
See [message routing diagnostics](/guide/messaging/subscriptions#diagnostics) for more.
:::

### Deduplication (JetStream `Nats-Msg-Id`)

Wolverine stamps a `Nats-Msg-Id` on every JetStream publish, so the stream's duplicate window discards
duplicates server-side — the idempotency key external (non-Wolverine) consumers can rely on, independent of
Wolverine's own durable-inbox dedup on `Envelope.Id`.

By default the id is the Wolverine `Envelope.Id`. Project a domain identity instead with `DeduplicateUsing`
so a logical event dedups even across separate sends (e.g. `{stream}/{version}`):

```csharp
opts.UseNats("nats://localhost:4222")
    .AutoProvision()
    // Any two publishes resolving to the same key within the stream's duplicate window collapse to one.
    .DeduplicateUsing(envelope => $"{envelope.GroupId}/{envelope.Id}");
```

Precedence for the dedup key:

1. An explicit `Nats-Msg-Id` header already on the outgoing envelope always wins.
2. Otherwise the configured `DeduplicateUsing` function is used.
3. Otherwise the Wolverine `Envelope.Id`.

The duplicate window itself is configured per stream (`WithDeduplicationWindow`) or transport-wide via
`JetStreamDefaults.DuplicateWindow` (default two minutes):

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineStream("ORDERS", s => s
        .WithSubjects("orders.>")
        .WithDeduplicationWindow(TimeSpan.FromMinutes(5)));
```

### Customizing the Publish Options <Badge type="tip" text="6.49" />

Wolverine sets `Nats-Msg-Id` and, for a scheduled message, the native scheduling headers. For every other
JetStream publish option, such as the `Nats-Expected-*` checks or a per-publish timeout, take the last word with
`ConfigureJetStreamPublish`. The callback receives the envelope and the `NatsJSPubOpts` Wolverine built, and the
publish uses whatever it returns:

```csharp
opts.UseNats("nats://localhost:4222")
    .AutoProvision()
    .ConfigureJetStreamPublish((envelope, pubOpts) => envelope.Message is OrderPlaced
        ? pubOpts with { ExpectedStream = "ORDERS" }
        : pubOpts);
```

A publish the server then refuses, for a failed expectation as for a full stream, fails the send the way
described under [JetStream](#jetstream). The callback is not applied to the copy a listener forwards to its
dead-letter subject.

## Scheduled Message Delivery

NATS Server 2.12+ can hold a message and deliver it at a given time. When the publishing endpoint qualifies,
Wolverine hands `ScheduleAsync()` and other scheduled sends to the server instead of scheduling them itself.

### Requirements

1. NATS Server version >= 2.12
2. The publishing endpoint uses JetStream (`ToNatsSubject(...).UseJetStream("ORDERS")`)
3. The stream is declared in the application with `EnableScheduledDelivery()` and keeps the default limits
   retention

### Configuration

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineStream("ORDERS", s => s
        .WithSubjects("orders.>")
        .EnableScheduledDelivery());

opts.PublishMessage<OrderCreated>()
    .ToNatsSubject("orders.created")
    .UseJetStream("ORDERS");

opts.ListenToNatsSubject("orders.created")
    .UseJetStream("ORDERS", "order-processor");
```

### How It Works

A scheduled send publishes a control message carrying:
- `Nats-Schedule: @at <RFC3339 timestamp>`
- `Nats-Schedule-Target: <destination subject>`

The transport automatically detects server version at startup.

NATS keeps one schedule per subject: a control message replaces the pending schedule on its subject. It also
rejects a control message published to its own target subject (`message schedules target is invalid`,
err 10190). Wolverine therefore publishes every control message to a **schedule subject** of its own — the
destination subject, a suffix (default `.scheduled`) and the envelope id, e.g.
`orders.created.scheduled.4f0c…` — while `Nats-Schedule-Target` stays the real destination (`orders.created`).
At the due time the server stores the message on the target subject, where the listener's consumer receives it,
and removes the control message. Until then the schedule lives in the stream, so it survives a restart or crash
of the sending and the receiving application.

The schedule subjects must be covered by the **same stream** as the target. They have two more tokens than the
target, so cover them with a `>` wildcard: `orders.>`, or `orders.created.scheduled.>` next to an exact
`orders.created`. An endpoint whose declared stream does not capture them — such as one declared with the exact
`orders.created.scheduled` earlier versions needed — logs a warning and uses Wolverine's own scheduling. Override
the suffix per publishing endpoint when needed:

```csharp
opts.PublishMessage<OrderCreated>()
    .ToNatsSubject("orders.created")
    .UseJetStream("ORDERS")
    .UseScheduleSubjectSuffix(".deferred");
```

A JetStream listener's consumer filters on the listener's own subject, so it never receives control messages.
A listener on a wildcard that also covers the schedule subjects, such as `orders.>`, acknowledges and skips
them; Wolverine recognizes them by the `Nats-Schedule` header. Core NATS listeners do not receive natively
scheduled messages at all: the server stores the message it materializes in the stream without publishing it
to core subscriptions, and a core listener on a wildcard skips the control messages it sees.

::: warning Limits retention only
Native scheduling needs a stream that keeps the control message until it fires. An interest stream
(`DefineWorkQueueStream()`, `AsWorkQueue()`) drops a control message no consumer is interested in, and on
interest and work-queue streams a consumer that covers the schedule subject deletes the control message by
acknowledging it. Wolverine therefore does not schedule natively on streams with interest or work-queue
retention, even with `EnableScheduledDelivery()`; scheduled sends to them use Wolverine's own scheduling, and
Wolverine logs that once per publishing endpoint.
:::

::: tip NATS Server 2.14.7 or later
Every schedule purges a subject, when it is published and again when it fires. Before 2.14.7 such a purge could
move a consumer with several filter subjects past messages on its other filters
([nats-server#8571](https://github.com/nats-io/nats-server/issues/8571)). Wolverine's own consumers filter on a
single subject, but consumers maintained outside the application may not.
:::

### Fallback Behavior

When native scheduled send is not available — a server older than 2.12, a publishing endpoint without JetStream,
a stream that is not declared in the application, lacks `EnableScheduledDelivery()` or uses interest or
work-queue retention — Wolverine schedules the message itself, persisted in its message store when one is
configured.

## Connecting to Multiple NATS Brokers

If a single Wolverine application needs to talk to more than one NATS broker, register the additional
broker(s) with `AddNamedNatsBroker` using a `BrokerName`, then pin publishing or listening to a specific
broker with the `*OnNamedBroker` overloads:

```csharp
opts.UseNats("nats://localhost:4222");

// An additional, independent NATS broker identified by name
opts.AddNamedNatsBroker(new BrokerName("secondary"), "nats://secondary-nats:4222");

// Or configure the additional broker with the full connection/auth surface
opts.AddNamedNatsBroker(new BrokerName("eu"), cfg =>
{
    cfg.ConnectionString = "nats://eu-nats:4222";
    cfg.EnableJetStream = true;
});

// Publish a message type to a subject on a named broker
opts.PublishMessage<OrderPlaced>()
    .ToNatsSubjectOnNamedBroker(new BrokerName("secondary"), "orders");

// Listen to a subject on a named broker
opts.ListenToNatsSubjectOnNamedBroker(new BrokerName("secondary"), "orders");
```

::: info
The Wolverine `Uri` scheme for any endpoint on a named broker is the broker name itself, so in the example
above you would see endpoint URIs like `secondary://subject/orders`. The default broker keeps the canonical
`nats://` scheme, which keeps the two brokers' endpoints from colliding.
:::

Connecting to multiple named brokers is distinct from [Multi-Tenancy](#multi-tenancy): a named broker is a
statically-addressed second connection that you target explicitly, whereas per-tenant connections are
selected at runtime from each message's tenant id.

## Multi-Tenancy

::: tip
For a holistic overview of multi-tenancy across all of Wolverine, see the [Multi-Tenancy Tutorial](/tutorials/multi-tenancy)
and [Multi-Tenancy with Wolverine](/guide/handlers/multi-tenancy) for how Wolverine tracks the tenant id across messages.
:::

The NATS transport supports two flavors of tenant isolation:

- **Subject-based** — all tenants share one connection and are separated by a tenant subject prefix
  (`{tenantId}.{subject}`). This is soft partitioning within a single NATS account.
- **Connection-based** — a tenant gets its own dedicated NATS connection to a different server or **account**.

::: info NATS accounts are the native tenancy boundary
In NATS, true multi-tenancy is [Accounts](https://docs.nats.io/running-a-nats-service/configuration/securing_nats/accounts):
each account is a fully isolated subject namespace, and a single connection authenticates into exactly **one**
account. So a genuinely isolated tenant means a **dedicated connection with its own credentials** (see
[Per-Tenant Connections](#per-tenant-connections)). A subject prefix on a shared connection is only
partitioning within one account, not account-level isolation.
:::

### Basic Multi-Tenancy (Subject Isolation)

```csharp
opts.UseNats("nats://localhost:4222")
    .ConfigureMultiTenancy(TenantedIdBehavior.TenantIdRequired)
    .AddTenant("tenant-a")
    .AddTenant("tenant-b");
```

### Tenant Behavior Options

- `TenantIdRequired`: Throws if tenant ID is missing
- `FallbackToDefault`: Uses base subject if tenant ID is missing

### Per-Tenant Connections

To route a tenant to its own NATS server or account, add it with a configuration action. The action runs when
the transport connects, against a copy of the transport's configuration as it is then — including settings made
after `AddTenant()` — so you only override what differs for this tenant: a different URL, or any of the NATS auth
mechanisms (token, JWT/NKey, credentials file, client certificate):

```csharp
opts.UseNats("nats://shared:4222")
    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
    .AddTenant("tenant-a", cfg => cfg.ConnectionString = "nats://tenant-a-host:4222")
    .AddTenant("tenant-b", cfg =>
    {
        cfg.ConnectionString = "nats://tenant-b-host:4222";
        cfg.CredentialsFile = "/etc/nats/tenant-b.creds";
    });
```

Each tenant with its own configuration gets a dedicated connection, owned by the transport for its lifetime.
Tenants added without a configuration action keep sharing the transport connection (subject-prefix isolation
only).

On the tenant's connection, these settings of the tenant's configuration apply:

- the connection itself: `ConnectionString`, `ClientName`, timeouts, authentication and TLS, and
  `ConfigureNatsOpts`
- `JetStreamDomain` / `JetStreamApiPrefix`, for every JetStream call over the tenant's connection
- the declared `Streams` and how they are provisioned: `AutoProvision`, `Provisioning` and
  `JetStreamDefaults.DuplicateWindow`

Everything else applies transport-wide, also to the tenant's connection: `EnableJetStream`, `DefaultQueueGroup`,
`NormalizeSubjects`, `MsgIdSource`, `ConfigureJetStreamPublish`, the tenant and subject resolvers,
`TenantSubjectPrefix`, `TrustTenantIdHeader`, the consumer defaults in `JetStreamDefaults` (`AckWait`, `MaxDeliver`,
`DeliverPolicy`) and the stream limits resource setup uses. The named consumers of listeners on the tenant's
connection follow the transport's `Provisioning()` as well. Startup logs a warning when a tenant's action changes
any of these. Whether a JetStream endpoint schedules messages natively follows the transport's stream declaration
and the shared server too, also when it sends to a tenant.

```csharp
opts.UseNats("nats://shared:4222")
    .DefineStream("ORDERS", s => s.WithSubjects("orders.>"))
    .AddTenant("tenant-c", cfg =>
    {
        cfg.ConnectionString = "nats://tenant-c-host:4222";
        cfg.JetStreamDomain = "tenant-c";

        // Streams on tenant-c's server: ORDERS from the transport, plus one of its own,
        // and only checked there rather than created
        cfg.Streams["AUDIT"] = new StreamConfiguration { Name = "AUDIT", Subjects = ["audit.>"] };
        cfg.Provisioning = NatsProvisioning.Verify;
    });
```

Both **sending and listening** are tenant-aware. A listener consumes on the shared connection *and* on each
tenant's dedicated connection: when a message arrives on a tenant connection it is stamped with that tenant's
id, and its ack/nak/dead-letter is routed back over the same connection. Sending a message tagged with a
`TenantId` publishes it over that tenant's connection. The streams the tenant's configuration declares — the
transport's, unless its action changed them — are provisioned on each tenant server as well, as the tenant's own
`AutoProvision` and `Provisioning` say.

### Where the Tenant Id Comes From

A message that arrives on a tenant's dedicated connection always belongs to that tenant. On the shared connection
a tenant-aware listener takes the tenant id from the message's `tenant-id` header, and otherwise from the subject's
tenant prefix — so any producer that may publish to the subject can act in any tenant's name. When producers you
do not control publish there, turn that off:

```csharp
opts.UseNats(nats =>
{
    nats.ConnectionString = "nats://shared:4222";
    nats.TrustTenantIdHeader = false;
})
.AddTenant("tenant-a")
.AddTenant("tenant-b");
```

The tenant id then comes only from the subject prefix, and only for a configured tenant; a message on the plain
subject has no tenant, whatever its header says. Keep the shared connection out of the tenants' accounts as well:
in the same account, the shared and the tenant's listener both receive a core NATS message, and on a JetStream
consumer they share, which of the two takes a message is left to chance.

### Tenants on Their Own Account

By default a tenant's subjects carry the tenant id as a prefix (`tenant-a.orders`), also on a tenant's dedicated
connection, where a listener then consumes `*.orders`. When each tenant has a NATS account of its own, the account
already keeps the tenants apart and their producers publish to the plain subjects. Give those tenants
`SameSubjectTenantMapper`, which sends to and listens on the plain subject over the tenant's connection; the
tenant id of an incoming message then comes from the connection it arrived on:

```csharp
opts.UseNats("nats://shared:4222")
    .ConfigureMultiTenancy(TenantedIdBehavior.TenantIdRequired)
    .AddTenant("tenant-a", new SameSubjectTenantMapper(), cfg =>
    {
        cfg.ConnectionString = "nats://hub:4222";
        cfg.CredentialsFile = "/etc/nats/tenant-a.creds";
    });
```

A listener uses each tenant's own mapper on that tenant's connection, and the transport's mapper on the shared
connection. A JetStream listener with a consumer name uses that name on every connection, so tenants that reach
the same stream in one account with different mappers — or a tenant and the shared connection — share the
consumer and overwrite each other's filter: keep such tenants in accounts of their own. `SameSubjectTenantMapper`
is meant for tenants with a connection of their own; on the shared connection a plain subject names no tenant.

### Custom Subject Mapper

```csharp
public class MyTenantMapper : ITenantSubjectMapper
{
    public string MapSubject(string baseSubject, string tenantId)
        => $"{tenantId}.{baseSubject}";
    
    public string? ExtractTenantId(string subject)
        => subject.Split('.').FirstOrDefault();
    
    public string GetSubscriptionPattern(string baseSubject)
        => $"*.{baseSubject}";
}

opts.UseNats("nats://localhost:4222")
    .UseTenantSubjectMapper(new MyTenantMapper());
```

## Request-Reply

Wolverine's request-reply pattern works with NATS:

```csharp
// Send and wait for response
var response = await bus.InvokeAsync<OrderConfirmation>(new CreateOrder(...));
```

The response endpoint always uses Core NATS for low-latency replies, even when the main endpoints use JetStream.

When a request goes to a core NATS subject that nothing subscribes to, the NATS server answers it with a
"no responders" status, and `InvokeAsync()` fails at once with a `WolverineRequestReplyException` instead of
waiting out its timeout <Badge type="tip" text="6.47" />. To tie that answer to the request, every request
carries its own reply subject on the wire — the node's reply subject plus a per-request token,
`wolverine.response.{service}.{node}.{token}` — and the reply listener also subscribes to
`wolverine.response.{service}.{node}.>`. A Wolverine responder answers on exactly the reply subject the request
carried, so it keeps working when its NATS user may only publish responses (`allow_responses`).

## Error Handling

### JetStream

- **Rejected publishes** <Badge type="tip" text="6.47" />: a JetStream publish the server refuses — the stream is
  full under `DiscardPolicy.New`, a `Nats-Expected-Last-Sequence` (or other `Nats-Expected-*`) check fails, the
  message is larger than the stream allows — fails the send with a `SendRejectedException` carrying the
  `NatsJSApiException` as its inner exception <Badge type="tip" text="6.49" />. A durable outbox keeps the message and
  applies its sending failure handling instead of deleting it as sent. On an endpoint configured with `SendInline()`
  and no sending failure policy that takes the exception, the refusal is not retried — no retry can change the
  server's answer — and `SendAsync()` throws the `SendRejectedException` to the caller rather than logging three
  retries and discarding the message. A publish the stream discards as a duplicate `Nats-Msg-Id` is still a
  successful send, because the stream already holds that message.
- **No PubAck**: a JetStream publish that gets no answer within the request timeout (`ConfigureTimeouts()`,
  default 30 seconds) fails with a `TimeoutException`, which the sending agent retries like any other send failure.
  Without that deadline NATS.Net kept waiting far longer while every server was down, and the sending agent waited
  with it.
- **Retry**: Message is requeued via `NakAsync()` with optional delay, up to the consumer's maximum delivery
  attempts (`JetStreamDefaults.MaxDeliver`, default 5, or a per-endpoint `MaxDeliveryAttempts` override).
- **Dead Letter**: When Wolverine's error handling moves a message to the error queue — once its retries are
  used up, or at once for `MoveToErrorQueue()` — the poison message is first forwarded to the configured
  dead-letter subject as a JetStream publish, and only once the stream has confirmed the copy is the original
  terminated on the consumer via `AckTerminateAsync(reason)`, so the server stops redelivering and records why.
  <Badge type="tip" text="6.47" />
  This no longer waits for the consumer's `MaxDeliver` to be used up: Buffered and Inline listeners have
  acknowledged the delivery by then, so a message moved to the error queue earlier used to be dropped. The copy
  carries its own `Nats-Msg-Id` (`{original}.dead-letter`), so a dead-letter subject inside the original's stream
  does not have it discarded as a duplicate of the original. It drops the original's other `Nats-*` headers —
  `Nats-Expected-*`, `Nats-TTL`, `Nats-Rollup`, `Nats-Schedule*` — which the server would otherwise apply to the
  dead-letter stream; the lowercase `nats-stream`, `nats-consumer`, `nats-delivered` and other diagnostics of the
  delivery stay. If **no** dead-letter subject
  is configured, Wolverine logs a warning and the message is terminated without being retained — configure a
  dead-letter subject to keep poison messages. Messages forwarded to the dead-letter subject carry the
  standard Wolverine diagnostic headers (`exception-type`, `exception-message`, `exception-stack`,
  `failed-at`, `original-destination`), with the delivery attempt count on the standard `attempts` header —
  see [diagnostic headers on dead letter messages](/tutorials/dead-letter-queues#diagnostic-headers-on-dead-letter-messages).
- **When the dead-letter copy fails**: no stream captures the dead-letter subject, or the stream refuses the
  copy (full under `DiscardPolicy.New`, larger than it allows). What happens to the original depends on the
  listener mode:

  | Listener | Original |
  |---|---|
  | `ProcessInline()`, `ProcessInParallelWithNativeAcks()` | Handed back to JetStream with a five second delay and delivered again, which counts against `MaxDeliver`; nothing is completed or recorded as dead lettered |
  | `BufferedInMemory()` (the default) | Acknowledged on receipt, so it is lost; Wolverine logs an error naming the message id |
  | `UseDurableInbox()` | Not affected: a durable listener dead letters into its message store |

::: warning The dead-letter subject needs a stream that keeps messages
The dead-letter copy is a JetStream publish, so a stream has to capture the dead-letter subject, and that stream
has to keep messages without a consumer: limits or work-queue retention, not interest retention
(`DefineWorkQueueStream()`, `AsWorkQueue()`), which discards a copy no consumer is interested in as soon as it
arrives.
:::

### Core NATS

- **Retry**: Message is republished to the subject
- **Dead Letter**: Handled by Wolverine's error handling policies

## Health Check

The NATS transport health check reports:

- **Unhealthy** while a NATS listener's last start failed, with the endpoint and the reason, until a later start of
  that listener succeeds. A listener that starts with the host fails the host instead, but one the agent framework
  starts afterwards — an exclusive listener, a shard of a global partitioned topology — can fail on its own, for
  example when its stream or its named consumer under `Provisioning(NatsProvisioning.Verify)` is missing. The
  record belongs to the node: when the agent framework gives the listener to another node after the failure, this
  node stays unhealthy until it restarts, so keep this check out of a liveness probe that restarts on it.
- **Degraded** while the transport is not connected yet.

## Auto-Provisioning

Enable automatic creation of streams and consumers:

```csharp
opts.UseNats("nats://localhost:4222")
    .AutoProvision();
```

Or use resource setup on startup:

```csharp
opts.Services.AddResourceSetupOnStartup();
```

### Existing Streams and Consumers <Badge type="tip" text="6.47" />

By default Wolverine creates a missing declared stream or JetStream listener consumer. A declared stream that
already exists is left as it is, because updating it can discard messages. A listener's named consumer that
already exists is brought in line with the configuration, so a changed `MaxDeliver` or `AckWait` just works.
`Provisioning()` changes what startup does with existing streams and consumers:

```csharp
opts.UseNats("nats://localhost:4222")
    // CreateOnly, CreateOrUpdate or Verify
    .Provisioning(NatsProvisioning.CreateOrUpdate)
    .DefineStream("ORDERS", s => s
        .WithSubjects("orders.>")
        .WithLimits(maxBytes: 10L * 1024 * 1024 * 1024));

opts.ListenToNatsSubject("orders.received")
    .UseJetStream("ORDERS", "order-processor")
    .ConfigureDeadLetterQueue(maxDeliveryAttempts: 10);
```

| `Provisioning(...)` | Missing stream or consumer | Existing stream that differs | Existing named consumer that differs |
|---|---|---|---|
| not set (default) | Created | Left alone | Updated |
| `CreateOnly` | Created | Left alone | Left alone |
| `CreateOrUpdate` | Created | Updated | Updated |
| `Verify` | Startup fails | Startup fails, listing every deviation | Startup fails, listing every deviation |

If a named consumer is maintained outside the application, for example with the NATS CLI, use
`Provisioning(NatsProvisioning.CreateOnly)` so Wolverine does not overwrite its `AckWait`, `MaxDeliver` or filter
with its own values.

Only the settings Wolverine itself writes are compared and updated: for a declared stream everything its
`StreamConfiguration` surfaces (subjects, retention, storage, limits, discard policy, replicas, duplicate window
and the allow/deny flags), and for a named consumer the explicit ack policy, `AckWait`, `MaxDeliver`, the filter
subject(s) and a `MaxAckPending` Wolverine sizes. An update is applied on top of the configuration the server
already has, so a description, metadata or any other setting maintained outside the application survives, and
so does a consumer's `DeliverPolicy`.

::: warning
`CreateOrUpdate` applies what the configuration says. Lowering `MaxBytes`, `MaxMessages` or `MaxAge` makes the
server discard messages to fit, and the server refuses changes JetStream does not allow on an existing stream or
consumer — a different storage type, a change to or from work-queue retention — which then fails the start.
:::

`Verify` is meant for streams and consumers provisioned outside the application, for example by infrastructure as
code. Stream deviations surface while the transport starts, so Wolverine's usual broker initialization retries
apply until `WolverineOptions.BrokerInitializationTimeout` elapses; a consumer deviation fails the listener as it
starts. `Verify` checks the declared streams whether or not `AutoProvision` is on, since it never creates
anything itself.

### Resource Commands

Only startup and resource setup provision declared streams; the other `resources` commands never create or
change one:

| Command | Declared streams |
|---|---|
| `resources setup` / `AddResourceSetupOnStartup()` | Created when missing — also with `AutoProvision` off, since setup is the explicit way to create broker objects — and existing ones handled as `Provisioning()` says; under `Verify` a missing or differing stream fails the setup |
| `resources check` | Read only. Fails for a missing stream of a JetStream endpoint and, under `CreateOrUpdate` or `Verify`, for one that differs from its declaration — whatever startup would create, change or fail on. It checks the streams the endpoints use on the shared connection, not the tenants' servers |
| `resources statistics`, `resources clear`, `resources teardown` | Left alone; they work under any `Provisioning()` setting. `teardown` deletes the named consumers of JetStream listeners |

Resource setup writes the same consumer settings the listener does, whatever the `Provisioning()` setting, so a
start after resource setup finds nothing to reconcile or to fail `Verify` on.

## Subject Prefix

When sharing a NATS server between multiple developers or development environments, you can add a prefix to all NATS subjects to isolate each environment's messaging. Use `WithSubjectPrefix()` or the generic `PrefixIdentifiers()` method:

```csharp
opts.UseNats("nats://localhost:4222")
    .WithSubjectPrefix("myapp");

// Subject "orders" becomes "myapp.orders"
```

You can also use `PrefixIdentifiersWithMachineName()` as a convenience to use the current machine name as the prefix:

```csharp
opts.UseNats("nats://localhost:4222")
    .PrefixIdentifiersWithMachineName();
```

## Complete Example

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseNats("nats://localhost:4222")
            .AutoProvision()
            .WithCredentials("user", "pass")
            .UseJetStream(js =>
            {
                js.MaxDeliver = 5;
                js.AckWait = TimeSpan.FromSeconds(30);
            })
            .DefineStream("ORDERS", s => s
                .WithSubjects("orders.>")
                .EnableScheduledDelivery());

        // Listen to orders with JetStream durability
        opts.ListenToNatsSubject("orders.received")
            .UseJetStream("ORDERS", "order-processor")
            .Named("order-listener");

        // Publish order events
        opts.PublishMessage<OrderCreated>()
            .ToNatsSubject("orders.created");

        opts.PublishMessage<OrderShipped>()
            .ToNatsSubject("orders.shipped");

        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
```

## Testing

To run tests locally:

```bash
# Start NATS with JetStream
docker run -d --name nats -p 4222:4222 -p 8222:8222 nats:latest --jetstream -m 8222

# For scheduled delivery tests, use NATS 2.14.7+
docker run -d --name nats -p 4222:4222 -p 8222:8222 nats:2.14.7 --jetstream -m 8222
```

## Global Partitioning

NATS subjects can be used as the external transport for [global partitioned messaging](/guide/messaging/partitioning#global-partitioning). This creates a set of sharded NATS subjects with companion local queues for sequential processing across a multi-node cluster.

Use `UseShardedNatsSubjects()` within a `GlobalPartitioned()` configuration:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseNats("nats://localhost:4222").AutoProvision();

        opts.MessagePartitioning.ByMessage<IMyMessage>(x => x.GroupId);

        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            // Creates 4 sharded NATS subjects named "orders1" through "orders4"
            // with matching companion local queues for sequential processing
            topology.UseShardedNatsSubjects("orders", 4);
            topology.MessagesImplementing<IMyMessage>();
        });
    }).StartAsync();
```

This creates NATS subjects named `orders1` through `orders4` with companion local queues `global-orders1` through `global-orders4`. Messages are routed to the correct shard based on their group id, and Wolverine handles the coordination between nodes automatically.

::: info JetStream is implied
Global partitioning forces every slot into durable mode, and a NATS endpoint can only be durable when it
is JetStream-backed. `UseShardedNatsSubjects()` therefore turns JetStream on for its own endpoints and
declares a stream per shard (named after the subject, upper-cased, dots replaced with underscores) so
`AutoProvision` creates it. Declare a stream of the same name yourself if you need different retention or
replication.
:::

::: tip Shard streams use work-queue retention <Badge type="tip" text="6.47" />
The shard streams `UseShardedNatsSubjects()` declares use JetStream's work-queue retention
(`StreamConfigRetention.Workqueue`), so a message waits in its shard until it is acknowledged even while no
node is consuming that shard — at startup, or while the shard moves to another node. Earlier versions declared
them with interest retention, which discards a message that arrives while no consumer is bound. This applies to
shard streams created from now on: Wolverine leaves an existing stream as it is, and the server refuses to change
an existing stream to or from work-queue retention, so recreate an old shard stream (once it is drained) to switch
it.
:::

## URI reference

The `NatsEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `nats://subject/{subject}` | `NatsEndpointUri.Subject("subject")` |

```csharp
using Wolverine.Nats;

var uri = NatsEndpointUri.Subject("orders.created");
```
