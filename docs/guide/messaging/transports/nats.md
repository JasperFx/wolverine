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
            .UseJetStream()
            .DefineWorkQueueStream("ORDERS", "orders.>");

        // Listen with JetStream consumer
        opts.ListenToNatsSubject("orders.received")
            .UseJetStream("ORDERS", "orders-consumer");

        // Publishing automatically uses JetStream when stream is defined
        opts.PublishAllMessages()
            .ToNatsSubject("orders.received");
    }).StartAsync();
```

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

## Scheduled Message Delivery

NATS Server 2.12+ supports native scheduled message delivery. When enabled, Wolverine uses NATS headers for scheduling instead of database persistence.

### Requirements

1. NATS Server version >= 2.12
2. Stream configured with `EnableScheduledDelivery()`

### Configuration

```csharp
opts.UseNats("nats://localhost:4222")
    .UseJetStream()
    .DefineWorkQueueStream("ORDERS", 
        s => s.EnableScheduledDelivery(), 
        "orders.>");
```

### How It Works

When conditions are met, scheduled messages use NATS headers:
- `Nats-Schedule: @at <RFC3339 timestamp>`
- `Nats-Schedule-Target: <subject>`

The transport automatically detects server version at startup.

### Fallback Behavior

When native scheduled send is not available (server < 2.12 or stream not configured), Wolverine falls back to its database-backed scheduled message persistence.

## Multi-Tenancy

NATS transport supports subject-based tenant isolation.

### Basic Multi-Tenancy

```csharp
opts.UseNats("nats://localhost:4222")
    .ConfigureMultiTenancy(TenantedIdBehavior.RequireTenantId)
    .AddTenant("tenant-a")
    .AddTenant("tenant-b");
```

### Tenant Behavior Options

- `RequireTenantId`: Throws if tenant ID is missing
- `FallbackToDefault`: Uses base subject if tenant ID is missing

### Custom Subject Mapper

```csharp
public class MyTenantMapper : ITenantSubjectMapper
{
    public string MapSubjectForTenant(string baseSubject, string tenantId)
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

- **Retry**: Message is requeued via `NakAsync()` with optional delay
- **Dead Letter**: Message is terminated via `AckTerminateAsync()`

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
