# Using Redis <Badge type="tip" text="5.0" />

## Installing

To use [Redis Streams](https://redis.io/docs/latest/develop/data-types/streams/) as a messaging transport for Wolverine, 
first install the `WolverineFx.Redis` Nuget package to your application. Behind the scenes, the `Wolverine.Redis` library
is using the [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) library. 

```bash
dotnet add package WolverineFx.Redis
```

::: tip
Since 6.32 the same package can also keep **entities and saga state** in Redis — a different feature
solving a different problem, with a durability caveat worth reading before you reach for it. See
[Redis Persistence](/guide/durability/redis). The two halves share nothing but the package and a
`StackExchange.Redis` dependency; use either, or both.
:::

## Using as Message Transport

To connect to Redis and configure listeners and senders, use this syntax:

<!-- snippet: sample_bootstrapping_with_redis -->
<a id='snippet-sample_bootstrapping_with_redis'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRedisTransport(RedisContainerFixture.ConnectionString)
            
            // Auto-create streams and consumer groups
            .AutoProvision()
            
            // Configure default consumer name selector for all Redis listeners
            .ConfigureDefaultConsumerName((runtime, endpoint) => 
                $"{runtime.Options.ServiceName}-{runtime.DurabilitySettings.AssignedNodeNumber}")
            
            // Useful for testing - auto purge queues on startup
            .AutoPurgeOnStartup();

        // Just publish all messages to Redis streams (uses database 0 by default)
        opts.PublishAllMessages().ToRedisStream("wolverine-messages");

        // Or explicitly configure message routing with database ID
        opts.PublishMessage<ColorMessage>()
            .ToRedisStream("colors", databaseId: 1)
            
            // Configure specific settings for this stream
            .BatchSize(50)
            .SendInline();

        // Listen to Redis streams with consumer groups (uses database 0 by default)
        opts.ListenToRedisStream("red", "color-processors")
            .ProcessInline()
            
            // Configure consumer settings
            .ConsumerName("red-consumer-1")
            .BatchSize(10)
            .BlockTimeout(TimeSpan.FromSeconds(5))
            
            // Start from beginning to consume existing messages (like Kafka's AutoOffsetReset.Earliest)
            .StartFromBeginning();

        // Listen to Redis streams with database ID specified
        opts.ListenToRedisStream("green", "color-processors", databaseId: 2)
            .BufferedInMemory()
            .BatchSize(25)
            .StartFromNewMessages(); // Default: only new messages (like Kafka's AutoOffsetReset.Latest)

        opts.ListenToRedisStream("blue", "color-processors", databaseId: 3)
            .UseDurableInbox()
            .ConsumerName("blue-consumer")
            .StartFromBeginning(); // Process existing messages too
            
        // Alternative: use StartFrom parameter directly
        opts.ListenToRedisStream("purple", "color-processors", StartFrom.Beginning)
            .BufferedInMemory();

        // This will direct Wolverine to try to ensure that all
        // referenced Redis streams and consumer groups exist at 
        // application start up time
        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L20-L80' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_redis' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Connection Options <Badge type="tip" text="6.9" />

`UseRedisTransport()` accepts four different connection sources. The connection string overload above
is the simplest, but you can also pass StackExchange.Redis [`ConfigurationOptions`](https://stackexchange.github.io/StackExchange.Redis/Configuration),
a fully caller-managed [`IConnectionMultiplexer`](https://stackexchange.github.io/StackExchange.Redis/Basics),
or a factory that resolves one from your IoC container:

```csharp
// 1. Connection string — Wolverine owns the ConnectionMultiplexer
opts.UseRedisTransport("localhost:6379");

// 2. ConfigurationOptions — Wolverine builds and owns the ConnectionMultiplexer,
//    but you control every StackExchange.Redis setting
var configuration = ConfigurationOptions.Parse("localhost:6379");
configuration.ConnectRetry = 5;
opts.UseRedisTransport(configuration);

// 3. A caller-managed IConnectionMultiplexer — you own its lifetime; Wolverine never disposes it
IConnectionMultiplexer multiplexer = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
opts.UseRedisTransport(multiplexer);

// 4. A factory resolved from the IoC container — share one multiplexer between Wolverine and
//    the rest of your application. The container owns it; Wolverine never disposes it.
opts.UseRedisTransport(sp => sp.GetRequiredService<IConnectionMultiplexer>());
```

With options (2), (3), and (4) Wolverine never has to recreate the connection from a static connection
string, which is what makes token-based authentication possible.

### Azure Managed Redis with Entra ID / Managed Identity

Azure Managed Redis access tokens expire and must be refreshed. The
[`Microsoft.Azure.StackExchangeRedis`](https://github.com/Azure/Microsoft.Azure.StackExchangeRedis) package
handles that refresh by augmenting a `ConfigurationOptions` (or the multiplexer it builds). Because Wolverine
can take that `ConfigurationOptions` (or the resulting `IConnectionMultiplexer`) directly, the connection
re-authenticates in place and the application no longer has to restart when a token expires:

```csharp
// Requires the Microsoft.Azure.StackExchangeRedis package
var configuration = await ConfigurationOptions
    .Parse("your-cache.region.redis.azure.net:10000")
    .ConfigureForAzureWithTokenCredentialAsync(new DefaultAzureCredential());

opts.UseRedisTransport(configuration);

// — or — build the multiplexer yourself and hand it to Wolverine:
// var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
// opts.UseRedisTransport(multiplexer);
```

::: tip
When you pass an `IConnectionMultiplexer` (option 3) or a factory that resolves one (option 4), Wolverine uses
it as-is and does **not** dispose it on shutdown — the multiplexer (and any token-refresh background work wired
into it) is owned by your application / IoC container. With the connection-string and `ConfigurationOptions`
overloads Wolverine owns the multiplexer it builds and disposes it for you.
:::

If you need to control the database id within Redis, you have these options:

<!-- snippet: sample_redis_database_configuration -->
<a id='snippet-sample_redis_database_configuration'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRedisTransport(RedisContainerFixture.ConnectionString);

        // Configure streams on different databases
        opts.PublishMessage<OrderCreated>()
            .ToRedisStream("orders", databaseId: 1);
            
        opts.PublishMessage<PaymentProcessed>()
            .ToRedisStream("payments", databaseId: 2);

        // Listen on different databases
        opts.ListenToRedisStream("orders", "order-processors", databaseId: 1);
        opts.ListenToRedisStream("payments", "payment-processors", databaseId: 2);
        
        // Advanced configuration with database ID
        opts.ListenToRedisStream("notifications", "notification-processors", databaseId: 3)
            .ConsumerName("notification-consumer-1")
            .BatchSize(100)
            .BlockTimeout(10.Seconds())
            .UseDurableInbox();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L85-L110' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_redis_database_configuration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To work with multiple databases in one application, see this sample:

<!-- snippet: sample_multiple_database_usage -->
<a id='snippet-sample_multiple_database_usage'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();

        // Different message types on different databases for isolation
        
        // Database 0: Default messages
        opts.PublishMessage<SystemEvent>().ToRedisStream("system-events");
        opts.ListenToRedisStream("system-events", "system-processors");
        
        // Database 1: Order processing
        opts.PublishMessage<OrderCreated>().ToRedisStream("orders", 1);
        opts.ListenToRedisStream("orders", "order-processors", 1);
        
        // Database 2: Payment processing  
        opts.PublishMessage<PaymentProcessed>().ToRedisStream("payments", 2);
        opts.ListenToRedisStream("payments", "payment-processors", 2);
        
        // Database 3: Analytics and reporting
        opts.PublishMessage<AnalyticsEvent>().ToRedisStream("analytics", 3);
        opts.ListenToRedisStream("analytics", "analytics-processors", 3);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L139-L164' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_multiple_database_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Connecting to Multiple Brokers <Badge type="tip" text="6.9" />

If a single Wolverine application needs to talk to more than one Redis broker, register the additional
broker(s) with `AddNamedRedisBroker` using a `BrokerName`, then pin publishing or listening to a specific
broker with the `*OnNamedBroker` overloads:

<!-- snippet: sample_redis_named_broker -->
<a id='snippet-sample_redis_named_broker'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The default Redis broker
        opts.UseRedisTransport("localhost:6379");

        // An additional, independent Redis broker identified by name
        opts.AddNamedRedisBroker(new BrokerName("secondary"), "localhost:6399");

        // Publish a message type to a stream on the named broker
        opts.PublishMessage<OrderCreated>()
            .ToRedisStreamOnNamedBroker(new BrokerName("secondary"), "orders");

        // Listen to a stream on the named broker
        opts.ListenToRedisStreamOnNamedBroker(new BrokerName("secondary"), "orders", "order-processors");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L169-L187' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_redis_named_broker' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
The Wolverine `Uri` scheme for any endpoint on a named broker is the broker name itself, so in the example
above you would see endpoint URIs like `secondary://stream/0/orders`. The default broker keeps the canonical
`redis://` scheme, which keeps the two brokers' endpoints from colliding.
:::

`AddNamedRedisBroker` has the same connection-source overloads as `UseRedisTransport`: a connection string,
a `ConfigurationOptions`, a caller-managed `IConnectionMultiplexer`, or a factory that resolves one from the
IoC container.

Connecting to multiple named brokers is distinct from [Multi-Tenancy](#multi-tenancy): a named broker is a
statically-addressed second connection that you target explicitly, whereas per-tenant connections are
selected at runtime from each message's tenant id.

## Multi-Tenancy <Badge type="tip" text="6.9" />

The Redis transport supports *broker-per-tenant* multi-tenancy: each tenant talks to its own dedicated Redis
server while sharing the same stream topology. Register a dedicated connection per tenant with `AddTenant`,
and Wolverine routes each message to the correct server from its `Envelope.TenantId`:

<!-- snippet: sample_redis_multi_tenancy -->
<a id='snippet-sample_redis_multi_tenancy'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRedisTransport("localhost:6379")
            .AutoProvision()

            // Route messages that carry a tenant id to that tenant's own Redis server;
            // messages with no (or an unknown) tenant id fall back to the shared connection
            .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)

            // Each tenant gets its own dedicated Redis server
            .AddTenant("tenant1", "redis-tenant1:6379")
            .AddTenant("tenant2", "redis-tenant2:6379");

        // The stream topology is shared; the connection is chosen per message from Envelope.TenantId
        opts.PublishMessage<OrderCreated>().ToRedisStream("orders");
        opts.ListenToRedisStream("orders", "order-processors");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L192-L212' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_redis_multi_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Outbound sends are routed by tenant id through the framework's `TenantedSender`; inbound, Wolverine runs one
listener per tenant connection and stamps each received envelope with its tenant id. `ConfigureMultiTenancy`
controls what happens for a message whose tenant id is unknown:

* `FallbackToDefault` (the default) — use the shared/default connection.
* `TenantIdRequired` — reject a message that has no tenant id.
* `IgnoreUnknownTenants` — silently drop messages for tenants that were never registered.

Each tenant is an independent Redis server, so the same stream key and consumer group are created separately
on each tenant's connection without colliding. As with named brokers, `AddTenant` accepts a connection
string, a `ConfigurationOptions`, or a caller-managed `IConnectionMultiplexer`; Wolverine disposes only the
multiplexers it builds itself.

## Interoperability

First, see the [tutorial on interoperability with Wolverine](/tutorials/interop) for general guidance. 

Next, the Redis transport supports interoperability through the `IRedisEnvelopeMapper` interface. If necessary, you
can build your own version of this mapper interface like the following:

<!-- snippet: sample_ourredisjsonmapper -->
<a id='snippet-sample_ourredisjsonmapper'></a>
```cs
// Simplistic envelope mapper that expects every message to be of
// type "T" and serialized as JSON that works perfectly well w/ our
// application's default JSON serialization
public class OurRedisJsonMapper<TMessage> : EnvelopeMapper<StreamEntry, List<NameValueEntry>>, IRedisEnvelopeMapper
{
    // Wolverine needs to know the message type name
    private readonly string _messageTypeName = typeof(TMessage).ToMessageTypeName();

    public OurRedisJsonMapper(Endpoint endpoint) : base(endpoint)
    {
        // Map the data property
        MapProperty(x => x.Data!, 
            (e, m) => e.Data = m.Values.FirstOrDefault(x => x.Name == "data").Value,
            (e, m) => m.Add(new NameValueEntry("data", e.Data)));
        
        // Set up the message type
        MapProperty(x => x.MessageType!,
            (e, m) => e.MessageType = _messageTypeName,
            (e, m) => m.Add(new NameValueEntry("message-type", _messageTypeName)));
        
        // Set up content type    
        MapProperty(x => x.ContentType!,
            (e, m) => e.ContentType = "application/json",
            (e, m) => m.Add(new NameValueEntry("content-type", "application/json")));
    }

    protected override void writeOutgoingHeader(List<NameValueEntry> outgoing, string key, string value)
    {
        outgoing.Add(new NameValueEntry($"header-{key}", value));
    }

    protected override bool tryReadIncomingHeader(StreamEntry incoming, string key, out string? value)
    {
        var target = $"header-{key}";
        foreach (var nv in incoming.Values)
        {
            if (nv.Name.Equals(target))
            {
                value = nv.Value.ToString();
                return true;
            }
        }

        value = null;
        return false;
    }

    protected override void writeIncomingHeaders(StreamEntry incoming, Envelope envelope)
    {
        var headers = incoming.Values.Where(k => k.Name.StartsWith("header-"));
        foreach (var nv in headers)
        {
            envelope.Headers[nv.Name.ToString()[7..]] = nv.Value.ToString(); // Remove "header-" prefix
        }

        // Capture the Redis stream message id
        envelope.Headers["redis-entry-id"] = incoming.Id.ToString();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L230-L291' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ourredisjsonmapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Durable Inbox <Badge type="tip" text="6.30" />

`UseDurableInbox()` on a Redis stream listener means exactly what it means on every other transport:
each message is written to your configured message store (`PersistMessagesWithPostgresql()` etc.) **before**
its stream entry is acknowledged, is handled from there, and scheduled retries are parked in the inbox. A
process crash mid-handler therefore replays the message from the inbox instead of losing it. Because the
entry is acknowledged as soon as it is durable, the consumer group's pending list stays short regardless of
how long handlers take.

::: warning
Before 6.30 the Redis stream endpoint was marked `IDatabaseBackedEndpoint`, which made a "durable" listener
skip the inbox write entirely and acknowledge the entry **on receipt** — effectively at-most-once on a crash,
and scheduled retries went to Redis's own scheduled set rather than the inbox. If you were running
`UseDurableInbox()` on Redis without a message store, that configuration now requires one, the same as for
RabbitMQ, Kafka or SQS. If what you actually wanted was Redis-native scheduled retries without a database,
use the default `BufferedInMemory()` (or `ProcessInline()`) listener: those schedule natively in Redis.
:::

## Native Acks with Parallel Processing <Badge type="tip" text="6.30" />

Redis stream listeners support `EndpointMode.NativeAck` (see [GH-3708](https://github.com/JasperFx/wolverine/issues/3708)),
which combines `BufferedInMemory`'s parallelism and group partitioning with `Inline`'s no-loss behavior, and needs no
database at all:

```csharp
opts.ListenToRedisStream("webhooks", "webhook-processors")
    .ProcessInParallelWithNativeAcks()
    .PartitionProcessingByGroupId(PartitionSlots.Seven)

    // XAUTOCLAIM is what recovers entries a dead node left pending
    .EnableAutoClaim(period: TimeSpan.FromSeconds(30), minIdle: TimeSpan.FromMinutes(2));
```

Incoming entries flow through an in-memory (optionally group-partitioned) execution block while the stream entry is
held **unacknowledged**, and are settled natively — `XACK` on handler success, dead-lettered or requeued on terminal
failure — from the completion continuation. Redis qualifies for this mode because `XACK` names a single entry id that
Wolverine carries on the envelope itself, so one delivery can be settled, out of order, from whichever worker thread
finished it.

The guarantee is exactly the one the mode promises everywhere: **messages sharing a group id never execute
concurrently**, and nothing is acknowledged until its handler succeeds. Processing in original delivery order is
*best effort*, not promised — a failed or reclaimed message re-enters its lane later, never concurrently. Use
`UseDurableInbox()` if you need strict ordering under failure.

Two things to know that are specific to Redis:

* **Recovery of a dead node's entries is opt-in.** Redis does not hand unacknowledged entries back when a connection
  drops the way RabbitMQ does; they stay in the consumer group's pending entries list until some consumer runs
  `XAUTOCLAIM`. That means `EnableAutoClaim()`. This is true of `Inline` and `BufferedInMemory` listeners as well.
* **Size `minIdle` above your slowest lane, not your slowest handler.** This mode holds an entry from the moment it is
  read until its handler finishes, including time spent queued in a lane. A `minIdle` shorter than the worst-case lane
  residency lets a consumer re-claim an entry it is still working on, and process it twice.

The read batch size is this transport's prefetch equivalent, and in this mode it defaults to twice the number of lanes
that can be busy at once — the partition slot count when `PartitionProcessingByGroupId()` is used, otherwise
`MaximumParallelMessages()` — instead of the usual 10. `BatchSize()` still overrides it. Note that unlike RabbitMQ's
prefetch, it does not cap how many entries can be unacknowledged: the bounded execution block is what applies back
pressure, and the consumer loop stops reading once that block is full.

## Deleting Stream Entries on Ack

By default Wolverine settles a handled entry with `XACK`, which clears it from the consumer group's pending entries
list but leaves it in the stream. `DeleteStreamEntryOnAck(true)` additionally removes the entry, so a stream consumed
by a single group does not grow without bound:

```csharp
opts.UseRedisTransport(connectionString)
    .DeleteStreamEntryOnAck(true);
```

::: warning Requires Redis 8.2 or later
This setting settles with `XACKDEL`, which was added in Redis 8.2. Against an older server every acknowledgement is
rejected, so **nothing is ever acknowledged** — entries accumulate in the pending entries list forever, and with
`EnableAutoClaim()` on they are re-claimed and reprocessed indefinitely. Wolverine therefore refuses to start a
listener when this setting meets a server that does not implement the command, and the exception tells you to either
upgrade the server or turn the setting off.

Wolverine deliberately does **not** fall back to `XACK` followed by `XDEL`. The two are not equivalent: `XDEL` removes
the entry for *every* consumer group on the stream rather than only the one acknowledging it, which would silently
destroy messages other groups have not read yet. That difference is why `XACKDEL` exists.

Note that the capability is detected by asking the server what commands it implements rather than by comparing version
numbers, so Redis-compatible servers that carry their own versioning — Valkey, DragonflyDB, and the managed cloud
offerings — are judged on what they actually support.
:::

## Scheduled Messaging <Badge type="tip" text="5.10" />

The Redis transport supports native Redis message scheduling for delayed or scheduled delivery. There's no configuration
necessary to utilize that.

## Dead Letter Queue Messages <Badge type="tip" text="5.10" />

For `Buffered` or `Inline` endpoints, you can use native Redis streams for "dead letter queue" messages using
the name "{StreamKey}:dead-letter". Each dead letter stream entry contains the serialized envelope plus the
standard Wolverine diagnostic headers as top-level entry fields — `exception-type`, `exception-message`,
`exception-stack`, `failed-at`, `original-destination` — alongside `message-type`, `envelope-id`, and
`attempts` fields so tooling can inspect failures without deserializing the envelope. See
[diagnostic headers on dead letter messages](/tutorials/dead-letter-queues#diagnostic-headers-on-dead-letter-messages)
for the full cross-transport header structure.

Enable the native dead letter queue like this:

<!-- snippet: sample_using_dead_letter_queue_for_redis -->
<a id='snippet-sample_using_dead_letter_queue_for_redis'></a>
```cs
var builder = Host.CreateDefaultBuilder();

using var host = await builder.UseWolverine(opts =>
{
    opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision()
        .SystemQueuesEnabled(false) // Disable reply queues
        .DeleteStreamEntryOnAck(true); // Clean up stream entries on ack

    // Sending inline so the messages are added to the stream right away
    opts.PublishAllMessages().ToRedisStream("wolverine-messages")
        .SendInline();

    opts.ListenToRedisStream("wolverine-messages", "default")
        .EnableNativeDeadLetterQueue() // Enable DLQ for failed messages
        .UseDurableInbox(); // Use durable inbox so retry messages are persisted
    
    // schedule retry delays
    // if durable, these will be scheduled natively in Redis
    opts.OnException<Exception>()
        .ScheduleRetry(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30));
    
    opts.Services.AddResourceSetupOnStartup();
}).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/Samples/RedisTransportWithScheduling.cs#L8-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_dead_letter_queue_for_redis' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Resetting in Tests

Redis stream endpoints implement the same `IBrokerQueue` surface as the relational database queue
transports, so [`IHost.ClearAllWolverineStorageAsync()`](/guide/testing.html#resetting-all-wolverine-storage-in-tests)
purges them alongside envelope storage. If you need the streams left alone, reset envelope storage
directly with `IMessageStoreAdmin.RebuildAsync()` instead.

## Global Partitioning

Redis streams can be used as the external transport for [global partitioned messaging](/guide/messaging/partitioning#global-partitioning). This creates a set of sharded Redis streams with companion local queues for sequential processing across a multi-node cluster.

Use `UseShardedRedisStreams()` within a `GlobalPartitioned()` configuration:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRedisTransport("localhost:6379").AutoProvision();

        opts.MessagePartitioning.ByMessage<IMyMessage>(x => x.GroupId);

        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            // Creates 4 sharded Redis streams named "orders1" through "orders4"
            // with matching companion local queues for sequential processing
            topology.UseShardedRedisStreams("orders", 4);
            topology.MessagesImplementing<IMyMessage>();
        });
    }).StartAsync();
```

This creates Redis streams named `orders1` through `orders4` with companion local queues `global-orders1` through `global-orders4`. Messages are routed to the correct shard based on their group id, and Wolverine handles the coordination between nodes automatically.

## URI reference

The `RedisEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `redis://stream/{databaseId}/{streamKey}` | `RedisEndpointUri.Stream("key", databaseId: 0)` |
| `redis://stream/{databaseId}/{streamKey}?consumerGroup={group}` | `RedisEndpointUri.Stream("key", 0, "group")` |

```csharp
using Wolverine.Redis;

var uri = RedisEndpointUri.Stream("orders", databaseId: 3);
```
