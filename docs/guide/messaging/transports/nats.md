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

## Aspire Integration

The recommended way to integrate Wolverine with .NET Aspire for NATS is to read the connection string injected by Aspire via `IConfiguration.GetConnectionString()`. Aspire injects the NATS URL when you use `.WithReference()` in the AppHost.

**AppHost** (`Aspire.Hosting.Nats` NuGet):
```csharp
var nats = builder.AddNATS("nats")
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

The override only applies to consumers Wolverine itself auto-provisions. If you reference a pre-created consumer by name with `UseJetStream(streamName, consumerName)`, Wolverine reuses that consumer's existing configuration regardless of `DeliverFrom(...)` — pre-creating the consumer with the desired policy via the NATS CLI or `JetStream` API is the right tool there.

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

#### Work Queue with Additional Configuration

```csharp
opts.UseNats("nats://localhost:4222")
    .DefineWorkQueueStream("ORDERS", 
        stream => stream.EnableScheduledDelivery(), 
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

### JetStream Domain

For multi-tenant or leaf node configurations:

```csharp
opts.UseNats("nats://localhost:4222")
    .UseJetStreamDomain("my-domain");
```

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

## Scheduled Message Delivery

NATS Server 2.12+ supports native scheduled message delivery. When enabled, Wolverine uses NATS headers for scheduling instead of database persistence.

### Requirements

1. NATS Server version >= 2.12
2. Stream configured with `EnableScheduledDelivery()`

### Configuration

```csharp
opts.UseNats("nats://localhost:4222")
    .UseJetStream(js => { })
    .DefineWorkQueueStream("ORDERS", 
        s => s.EnableScheduledDelivery(), 
        "orders.>");
```

### How It Works

When conditions are met, scheduled messages use NATS headers:
- `Nats-Schedule: @at <RFC3339 timestamp>`
- `Nats-Schedule-Target: <destination subject>`

The transport automatically detects server version at startup.

NATS requires the scheduling (control) message to be published to a subject that is **different** from
`Nats-Schedule-Target` — publishing both to the same subject is rejected with
`message schedules target is invalid` (err 10190). Wolverine therefore publishes the control message to a
derived **schedule subject** — the destination subject plus a suffix (default `.scheduled`, e.g.
`orders.created.scheduled`) — while `Nats-Schedule-Target` stays the real destination
(`orders.created`). At the scheduled time the server materializes a new message onto the target subject,
where your listener's consumer receives it; the control message itself is never delivered to consumers.

Both the target and the derived schedule subject must be covered by the **same stream**. The schedule
subject is the target plus an extra suffix token (`orders.created` → `orders.created.scheduled`), so it
always has one more token than the target. Any filter that only matches the target's token count — an
exact-subject filter, or a `*` pattern such as `orders.*` — therefore covers the target but **not**
`<subject>.scheduled`. Cover both with a `>`-style prefix wildcard such as `orders.>`, or list the target
and schedule subjects as explicit filters. Override the suffix per publishing endpoint when needed:

```csharp
opts.PublishMessage<OrderCreated>()
    .ToNatsSubject("orders.created")
    .UseJetStream("ORDERS")
    .UseScheduleSubjectSuffix(".deferred");
```

### Fallback Behavior

When native scheduled send is not available (server < 2.12 or stream not configured), Wolverine falls back to its database-backed scheduled message persistence.

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

To route a tenant to its own NATS server or account, add it with a configuration action. The action receives
a copy of the transport's own connection settings, so you only override what differs for this tenant — a
different URL, or any of the NATS auth mechanisms (token, JWT/NKey, credentials file, client certificate):

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

Both **sending and listening** are tenant-aware. A listener consumes on the shared connection *and* on each
tenant's dedicated connection: when a message arrives on a tenant connection it is stamped with that tenant's
id, and its ack/nak/dead-letter is routed back over the same connection. Sending a message tagged with a
`TenantId` publishes it over that tenant's connection. If any tenant streams need JetStream, the configured
streams are auto-provisioned on each tenant server as well (when `AutoProvision()` is on).

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

## Error Handling

### JetStream

- **Retry**: Message is requeued via `NakAsync()` with optional delay, up to the consumer's maximum delivery
  attempts (`JetStreamDefaults.MaxDeliver`, default 5, or a per-endpoint `MaxDeliveryAttempts` override).
- **Dead Letter**: Once delivery attempts are exhausted, the poison message is first forwarded to the
  configured dead-letter subject (so a terminate failure can't lose it), then terminated on the consumer via
  `AckTerminateAsync(reason)` so the server stops redelivering and records why. If **no** dead-letter subject
  is configured, Wolverine logs a warning and the message is terminated without being retained — configure a
  dead-letter subject to keep poison messages.

### Core NATS

- **Retry**: Message is republished to the subject
- **Dead Letter**: Handled by Wolverine's error handling policies

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
            .DefineWorkQueueStream("ORDERS", 
                s => s.EnableScheduledDelivery(), 
                "orders.>");

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

# For scheduled delivery tests, use NATS 2.12+
docker run -d --name nats -p 4222:4222 -p 8222:8222 nats:2.12-alpine --jetstream -m 8222
```

## URI reference

The `NatsEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `nats://subject/{subject}` | `NatsEndpointUri.Subject("subject")` |

```csharp
using Wolverine.Nats;

var uri = NatsEndpointUri.Subject("orders.created");
```
