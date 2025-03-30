using System.Text.Json;
using Amazon.SimpleNotificationService.Model;
using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSns;

public class
    AmazonSnsSubscriberConfiguration : SubscriberConfiguration<AmazonSnsSubscriberConfiguration, AmazonSnsTopic>
{
    internal AmazonSnsSubscriberConfiguration(AmazonSnsTopic endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure how the topic should be created within SNS
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration ConfigureTopicCreation(Action<CreateTopicRequest> configure)
    {
        add(e => configure(e.Configuration));
        return this;
    }

    /// Opt to send messages as raw JSON without any Wolverine metadata
    /// </summary>
    /// <param name="defaultMessageType">Optional. If both sending and receiving from this queue, you will want to specify a default message type</param>
    /// <param name="configure">Optional configuration of System.Text.Json for this endpoint</param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration SendRawJsonMessage(Type? defaultMessageType = null,
        Action<JsonSerializerOptions>? configure = null)
    {
        var options = new JsonSerializerOptions();
        configure?.Invoke(options);
        add(e => e.Mapper = new RawJsonSnsEnvelopeMapper(defaultMessageType ?? typeof(object), options));

        return this;
    }

    /// <summary>
    /// Utilize custom envelope mapping for SNS interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration InteropWith(ISnsEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
        return this;
    }

    /// <summary>
    ///     Subscribes the given SQS queue to the current SNS topic
    /// </summary>
    /// <param name="queueName">The name of the SQS queue</param>
    /// <param name="rawMessageDelivery">Enables raw message delivery to Amazon SQS or HTTP/S endpoints</param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration SubscribeSqsQueue(string queueName, bool rawMessageDelivery = false)
    {
        add(e => e.TopicSubscriptions.Add(new AmazonSnsSubscription(queueName, rawMessageDelivery,
            AmazonSnsSubscriptionType.Sqs)));
        return this;
    }
}
