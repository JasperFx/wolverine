using Microsoft.Extensions.Hosting;
using JasperFx.Resources;

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

        #endregion
    }
}