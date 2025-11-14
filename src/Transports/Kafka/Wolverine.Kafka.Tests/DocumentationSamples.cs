using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Wolverine.Util;

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
                    // This is NOT combinatorial with the ConfigureConsumers() call above
                    // and completely replaces the parent configuration
                    .ConfigureConsumer(config =>
                    {
                        // This will also set the Envelope.GroupId for any
                        // received messages at this topic
                        config.GroupId = "foo";
                        config.BootstrapServers = "localhost:9092";

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

    public static async Task disable_producing()
    {
        #region sample_disable_all_kafka_sending

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

        #endregion
    }

    public static async Task use_named_brokers()
    {
        #region sample_using_multiple_kafka_brokers

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

#region sample_OurKafkaJsonMapper

// Simplistic envelope mapper that expects every message to be of
// type "T" and serialized as JSON that works perfectly well w/ our
// application's default JSON serialization
public class OurKafkaJsonMapper<TMessage> : IKafkaEnvelopeMapper
{
    // Wolverine needs to know the 
    private readonly string _messageTypeName = typeof(TMessage).ToMessageTypeName();

    // Map the Wolverine Envelope structure to the outgoing Kafka structure
    public void MapEnvelopeToOutgoing(Envelope envelope, Message<string, byte[]> outgoing)
    {
        // We'll come back to this later...
        throw new NotSupportedException();
    }

    // Map the incoming message from Kafka to the incoming Wolverine envelope
    public void MapIncomingToEnvelope(Envelope envelope, Message<string, byte[]> incoming)
    {
        // We're making an assumption here that only one type of message
        // is coming in on this particular Kafka topic, so we're telling
        // Wolverine what the message type is according to Wolverine's own
        // message naming scheme
        envelope.MessageType = _messageTypeName;

        // Tell Wolverine to use JSON serialization for the message 
        // data
        envelope.ContentType = "application/json";

        // Put the raw binary data right on the Envelope where
        // Wolverine "knows" how to get at it later
        envelope.Data = incoming.Value;
    }
}

#endregion

/*
// Who knows, maybe the upstream app uses a different JSON naming
// scheme than our .NET message types, so let's have the ability
// to specify JSON serialization policies just in case
_options = options;
*/

