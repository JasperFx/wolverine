using System.Text;
using Confluent.Kafka;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka;

public static class KafkaTransportExtensions
{
    /// <summary>
    ///     Quick access to the Kafka Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static KafkaTransport KafkaTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<KafkaTransport>();
    }

    /// <summary>
    /// Add a connection to an Kafka broker within this application
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static KafkaTransportExpression UseKafka(this WolverineOptions options, string bootstrapServers)
    {
        var transport = options.KafkaTransport();
        transport.ConsumerConfig.BootstrapServers = bootstrapServers;
        transport.ProducerConfig.BootstrapServers = bootstrapServers;
        transport.AdminClientConfig.BootstrapServers = bootstrapServers;

        return new KafkaTransportExpression(transport, options);
    }

    /// <summary>
    /// Make additive configuration to the Kafka integration for this application
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static KafkaTransportExpression ConfigureKafka(this WolverineOptions options, string bootstrapServers)
    {
        var transport = options.KafkaTransport();

        return new KafkaTransportExpression(transport, options);
    }

    /// <summary>
    ///     Listen for incoming messages at the designated Kafka topic name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="topicName">The name of the Rabbit MQ queue</param>
    /// <param name="configure">
    ///     Optional configuration for this Rabbit Mq queue if being initialized by Wolverine
    ///     <returns></returns>
    public static KafkaListenerConfiguration ListenToKafkaTopic(this WolverineOptions endpoints, string topicName)
    {
        var transport = endpoints.KafkaTransport();

        var endpoint = transport.Topics[topicName];
        endpoint.EndpointName = topicName;
        endpoint.IsListener = true;

        return new KafkaListenerConfiguration(endpoint);
    }

    /// <summary>
    /// Publish messages to an Kafka topic
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName"></param>
    /// <returns></returns>
    public static KafkaSubscriberConfiguration ToKafkaTopic(this IPublishToExpression publishing, string topicName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<KafkaTransport>();

        var topic = transport.Topics[topicName];

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new KafkaSubscriberConfiguration(topic);
    }

    /// <summary>
    /// Publish messages to Kafka topics based on Wolverine's rules for deriving topic
    /// names from a message type
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName"></param>
    /// <returns></returns>
    public static KafkaSubscriberConfiguration ToKafkaTopics(this IPublishToExpression publishing)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<KafkaTransport>();

        var topic = transport.Topics[KafkaTopic.WolverineTopicsName];

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new KafkaSubscriberConfiguration(topic);
    }

    internal static Envelope CreateEnvelope(this IKafkaEnvelopeMapper mapper, string topicName, Message<string, string> message)
    {
        var envelope = new Envelope
        {
            GroupId = message.Key,
            Data = Encoding.Default.GetBytes(message.Value),
            TopicName = topicName
        };

        message.Headers ??= new Headers(); // prevent NRE

        mapper.MapIncomingToEnvelope(envelope, message);

        return envelope;
    }

    internal static Message<string, string> CreateMessage(this IKafkaEnvelopeMapper mapper, Envelope envelope)
    {
        var message = new Message<string, string>
        {
            Key = envelope.GroupId,
            Value = Encoding.Default.GetString(envelope.Data),
            Headers = new Headers()
        };

        mapper.MapEnvelopeToOutgoing(envelope, message);

        return message;
    }
}