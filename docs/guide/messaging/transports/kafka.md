# Using Kafka

::: warning
The Kafka transport does not really support the "Requeue" error handling policy in Wolverine. "Requeue" in this case becomes
effectively an inline "Retry"
:::

## Installing

To use [Kafka](https://www.confluent.io/what-is-apache-kafka/) as a messaging transport with Wolverine, first install the `Wolverine.Kafka` library via nuget to your project. Behind the scenes, this package uses the [Confluent.Kafka client library](https://github.com/confluentinc/confluent-kafka-dotnet) managed library for accessing Kafka brokers.

```bash
dotnet add WolverineFx.Kafka
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
        opts.UseKafka("localhost:9092")

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
                config.BootstrapServers = "localhost:9092";

                // Other configuration
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L14-L126' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_kafka' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The various `Configure*****()` methods provide quick access to the full API of the Confluent Kafka library for security
and fine tuning the Kafka topic behavior. 

## Listener Consumer Settings <Badge type="tip" text="5.16" />

When building a Kafka listener, Wolverine configures the underlying Confluent Kafka `ConsumerConfig` differently
depending on whether the listener endpoint is **durable** (backed by the transactional inbox) and how the listener
processes messages. Understanding these settings is important for getting the delivery guarantees you need.

### How Endpoint Mode Affects Consumer Configuration

When an endpoint uses `EndpointMode.Durable` (i.e., you've called `.UseDurableInbox()` or applied durable inbox
globally), Wolverine overrides two key consumer settings before building the listener:

| Consumer Setting | Durable (`UseDurableInbox`) | Non-Durable (`BufferedInMemory` / `Inline`) |
|---|---|---|
| `EnableAutoCommit` | `false` | `true` (Kafka default) |
| `EnableAutoOffsetStore` | `false` | `true` (Kafka default) |

In **durable mode**, Wolverine disables Kafka's automatic offset management so that offsets are only committed
after a message has been successfully processed and persisted to the transactional inbox. This prevents message loss
if the application shuts down unexpectedly -- unprocessed messages will be re-delivered when the consumer rejoins
the group.

In **non-durable mode** (`BufferedInMemory` or `ProcessInline`), Kafka's default auto-commit behavior is left
in place. The Kafka client library periodically commits offsets automatically, which provides higher throughput
at the cost of potential message loss during an ungraceful shutdown.

### Offset Commit Behavior in the Listener

Regardless of endpoint mode, the `KafkaListener` calls `_consumer.Commit()` in these situations:

- **On successful processing** -- `CompleteAsync()` explicitly commits the consumer offset after a message
  finishes processing. In durable mode this is the *only* path that advances the offset.
- **On poison pill messages** -- If an incoming Kafka message cannot be deserialized into a Wolverine envelope
  at all (a true poison pill), the listener commits the offset to skip past the bad message and avoid blocking
  the consumer.
- **On dead letter queue routing** -- When a message exhausts all retries and is moved to the native dead letter
  queue topic, the offset is committed after the DLQ produce succeeds.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/when_publishing_and_receiving_by_partition_key.cs#L13-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_kafka_by_partition_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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
        opts.UseKafka("localhost:9092").AutoProvision();
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
        opts.UseKafka("localhost:9092").AutoProvision();
        opts.Policies.DisableConventionalLocalRouting();

        opts.Services.AddResourceSetupOnStartup();

        opts.PublishAllMessages().ToKafkaTopic("json")
            
            // Just publish the outgoing information as pure JSON
            // and no other Wolverine metadata
            .PublishRawJson(new JsonSerializerOptions());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/publish_and_receive_raw_json.cs#L21-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_raw_json_sending_and_receiving_with_kafka' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Instrumentation & Diagnostics <Badge type="tip" text="3.13" />

When receiving messages through Kafka and Wolverine, there are some useful elements of Kafka metadata
on the Wolverine `Envelope` you can use for instrumentation or diagnostics as shown in this sample middleware:

<!-- snippet: sample_KafkaInstrumentation_middleware -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L178-L191' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_kafkainstrumentation_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Connecting to Multiple Brokers <Badge type="tip" text="4.7" />

Wolverine supports interacting with multiple Kafka brokers within one application like this:

<!-- snippet: sample_using_multiple_kafka_brokers -->
<a id='snippet-sample_using_multiple_kafka_brokers'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseKafka("localhost:9092");
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L151-L174' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_multiple_kafka_brokers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that the `Uri` scheme within Wolverine for any endpoints from a "named" Kafka broker is the name that you supply
for the broker. So in the example above, you might see `Uri` values for `emea://colors` or `americas://red`.

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
            .UseKafka("localhost:9092")
            
            // Tell Wolverine that this application will never
            // produce messages to turn off any diagnostics that might
            // try to "ping" a topic and result in errors
            .ConsumeOnly();
        
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L131-L146' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_all_kafka_sending' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
