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
            .ConfigureConsumer(config =>
            {
                // This will also set the Envelope.GroupId for any
                // received messages at this topic
                config.GroupId = "foo";
                
                // Other configuration
            });

        opts.ListenToKafkaTopic("green")
            .BufferedInMemory();

        // This will direct Wolverine to try to ensure that all
        // referenced Kafka topics exist at application start up
        // time
        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L11-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_kafka' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The various `Configure*****()` methods provide quick access to the full API of the Confluent Kafka library for security
and fine tuning the Kafka topic behavior. 

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/publish_and_receive_raw_json.cs#L21-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_raw_json_sending_and_receiving_with_kafka' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L98-L111' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_kafkainstrumentation_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
