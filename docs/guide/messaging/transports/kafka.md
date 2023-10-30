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
        opts.UseKafka("localhost:29092")
            
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
            });

        // Just publish all messages to Kafka topics
        // based on the message type (or message attributes)
        // This will get fancier in the near future
        opts.PublishAllMessages().ToKafkaTopics();
 
        // Or explicitly make subscription rules
        opts.PublishMessage<ColorMessage>()
            .ToKafkaTopic("colors");
 
        // Listen to topics
        opts.ListenToKafkaTopic("red")
            .ProcessInline();

        opts.ListenToKafkaTopic("green")
            .BufferedInMemory();
 

        // This will direct Wolverine to try to ensure that all
        // referenced Kafka topics exist at application start up 
        // time
        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Kafka/Wolverine.Kafka.Tests/DocumentationSamples.cs#L10-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_kafka' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The various `Configure*****()` methods provide quick access to the full API of the Confluent Kafka library for security
and fine tuning the Kafka topic behavior. 
