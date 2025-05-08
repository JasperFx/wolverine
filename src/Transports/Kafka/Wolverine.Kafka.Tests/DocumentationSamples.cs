using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oakton.Resources;

namespace Wolverine.Kafka.Tests;

public class DocumentationSamples
{
    public static async Task configure()
    {
        #region sample_bootstrapping_with_kafka

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

        #endregion
    }
}

#region sample_KafkaInstrumentation_middleware

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

#endregion