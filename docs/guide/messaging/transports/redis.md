# Using Redis <Badge type="tip" text="5.0" />

## Installing

To use [Redis Streams](https://redis.io/docs/latest/develop/data-types/streams/) as a messaging transport for Wolverine, 
first install the `WolverineFx.Redis` Nuget package to your application. Behind the scenes, the `Wolverine.Redis` library
is using the [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) library. 

```bash
dotnet add package WolverineFx.Redis
```

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L19-L79' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_redis' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L84-L109' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_redis_database_configuration' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L138-L163' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_multiple_database_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Redis/Wolverine.Redis.Tests/DocumentationSamples.cs#L181-L242' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ourredisjsonmapper' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Scheduled Messaging <Badge type="tip" text="5.10" />

The Redis transport supports native Redis message scheduling for delayed or scheduled delivery. There's no configuration
necessary to utilize that.

## Dead Letter Queue Messages <Badge type="tip" text="5.10" />

For `Buffered` or `Inline` endpoints, you can use native Redis streams for "dead letter queue" messages using
the name "{StreamKey}:dead-letter":

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
