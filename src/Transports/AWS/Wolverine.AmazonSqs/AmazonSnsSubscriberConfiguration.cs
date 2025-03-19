using System.Text.Json;
using Amazon.SimpleNotificationService.Model;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs;

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
    
    /// <summary>
    ///     Configure the SNS topic subscriptions. Currently only support SQS subscriptions, as HTTPs/Emails need to be
    ///     confirmed after creations
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration ConfigureTopicSubscriptions(Action<ICollection<SubscribeRequest>> configure)
    {
        add(e => configure(e.TopicSubscriptions));
        return this;
    }
    
    /// <summary>
    ///     Subscribes the SQS queue with the given ARN to the current SNS topic
    /// </summary>
    /// <param name="queueArn">The ARN of the SQS queue</param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration SubscribeSqsQueueByArn(string queueArn)
    {
        add(e => e.TopicSubscriptions.Add(new SubscribeRequest{ TopicArn = e.EndpointArn, Protocol = "sqs", Endpoint = queueArn}));
        return this;
    }
    
    /// Opt to send messages as raw JSON without any Wolverine metadata
    /// </summary>
    /// <param name="defaultMessageType">Optional. If both sending and receiving from this queue, you will want to specify a default message type</param>
    /// <param name="configure">Optional configuration of System.Text.Json for this endpoint</param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration SendRawJsonMessage(Type? defaultMessageType = null, Action<JsonSerializerOptions>? configure = null)
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
}
