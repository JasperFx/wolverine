using System.Text.Json;
using Amazon.SimpleNotificationService.Model;
using Newtonsoft.Json;
using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Runtime.Serialization;

namespace Wolverine.AmazonSns;

public class AmazonSnsSubscriberConfiguration : SubscriberConfiguration<AmazonSnsSubscriberConfiguration, AmazonSnsTopic>
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
    /// Create a completely customized mapper using the WolverineRuntime and the current
    /// Endpoint. This is built lazily at system bootstrapping time
    /// </summary>
    /// <param name="factory"></param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration UseInterop(Func<AmazonSnsTopic, ISnsEnvelopeMapper> factory)
    {
        add(e => e.Mapper = factory(e));
        return this;
    }

    /// <summary>
    ///     Subscribes the given SQS queue to the current SNS topic
    /// </summary>
    /// <param name="queueName">The name of the SQS queue</param>
    /// <param name="configure">Optional configuration of the SNS subscription attributes</param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration SubscribeSqsQueue(string queueName, Action<AmazonSnsSubscriptionAttributes>? configure = null)
    {
        var attributes = new AmazonSnsSubscriptionAttributes();
        configure?.Invoke(attributes);
        add(e =>
        {
            e.TopicSubscriptions.Add(new AmazonSnsSubscription(queueName, AmazonSnsSubscriptionType.Sqs, attributes));
            e.Parent.SQS.Queues.FillDefault(queueName);
        });
        
        return this;
    }
    
    
    /// <summary>
    /// Use an NServiceBus compatible enveloper mapper to interact with NServiceBus systems on the other end
    /// </summary>
    /// <returns></returns>
    /// <param name="replyQueueName">Name of an SQS queue where NServiceBus should send resplies back to this application</param>
    public AmazonSnsSubscriberConfiguration UseNServiceBusInterop(string? replyQueueName)
    {
        add(e =>
        {
            e.DefaultSerializer = new NewtonsoftSerializer(new JsonSerializerSettings());
            e.Mapper = new NServiceBusEnvelopeMapper(replyQueueName, e);
        });
        
        return this;
    }

    /// <summary>
    /// Use a MassTransit compatible envelope mapper to interact with MassTransit systems on the other end
    /// </summary>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration UseMassTransitInterop()
    {
        add(e => e.Mapper = new MassTransitMapper(Endpoint as IMassTransitInteropEndpoint));
        return this;
    }
    
        
    /// <summary>
    /// Interop with upstream systems by reading messages with the CloudEvents specification
    /// </summary>
    /// <param name="jsonSerializerOptions"></param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration InteropWithCloudEvents(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        add(e =>
        {
            e.MapperFactory = (queue, r) =>
            {
                var mapper = e.BuildCloudEventsMapper(r, jsonSerializerOptions);
                e.DefaultSerializer = mapper;
                return new CloudEventsSnsMapper(mapper);
            };
        });

        return this;
    }
}
