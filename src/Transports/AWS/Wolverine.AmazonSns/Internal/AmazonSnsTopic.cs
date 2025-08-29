using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.AmazonSns.Internal;

public class AmazonSnsTopic : Endpoint, IBrokerQueue
{
    private bool _initialized;

    internal AmazonSnsTopic(string topicName, AmazonSnsTransport parent) 
        : base(new Uri($"{AmazonSnsTransport.SnsProtocol}://{topicName}"), EndpointRole.Application)
    {
        Parent = parent;
        
        TopicName = topicName;
        EndpointName = topicName;
        TopicArn = string.Empty;

        Configuration = new CreateTopicRequest(TopicName);
        
        MessageBatchSize = 10;
    }

    internal AmazonSnsTransport Parent { get; }

    /// <summary>
    ///     Pluggable strategy for interoperability with non-Wolverine systems. Customizes how the incoming SNS requests
    ///     are read and how outgoing messages are written to SNS
    /// </summary>
    public ISnsEnvelopeMapper? Mapper { get; set; }
    
    
    internal Func<AmazonSnsTopic, IWolverineRuntime, ISnsEnvelopeMapper>? MapperFactory = null;
    
    internal ISnsEnvelopeMapper BuildMapper(IWolverineRuntime runtime)
    {
        if (Mapper != null)
        {
            return Mapper;
        }

        if (MapperFactory != null)
        {
            return MapperFactory(this, runtime);
        }

        return new DefaultSnsEnvelopeMapper();
    }
    
    public string TopicName { get; }
    public string TopicArn { get; set; }
    
    public CreateTopicRequest Configuration { get; }
    public IList<AmazonSnsSubscription> TopicSubscriptions { get; set; } = new List<AmazonSnsSubscription>();
    
    public async ValueTask<bool> CheckAsync()
    {
        return await Parent.SnsClient!.FindTopicAsync(TopicName) is not null;
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        var client = Parent.SnsClient!;

        if (TopicArn.IsEmpty())
        {
            var topic = await client.FindTopicAsync(TopicName);
            if (topic is null) return;
            TopicArn = topic.TopicArn;
        }

        foreach (var subscription in TopicSubscriptions)
        {
            var unsubscribeRequest = new UnsubscribeRequest(subscription.SubscriptionArn);
            await client.UnsubscribeAsync(unsubscribeRequest);
        }
        
        await client.DeleteTopicAsync(TopicArn);
    }

    public ValueTask SetupAsync(ILogger logger)
    {
        return new ValueTask(setupAsync(Parent.SnsClient!));
    }
    
    public ValueTask PurgeAsync(ILogger logger)
    {
        // TODO We can't really purge SNS topics, so probably do nothing here
        return new ValueTask(Task.CompletedTask);
    }
    
    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var client = Parent.SnsClient!;

        await loadTopicArnIfEmptyAsync(client);
        
        var atts = await client.GetTopicAttributesAsync(TopicArn);
        atts.Attributes.Add("name", TopicName);
        
        // TODO return all attributes?
        return atts.Attributes;
    }
    
    internal async Task SendMessageAsync(Envelope envelope, ILogger logger)
    {
        if (!_initialized)
        {
            await InitializeAsync(logger);
        }
        
        var body = Mapper!.BuildMessageBody(envelope);
        var request = new PublishRequest(TopicArn, body);
        if (envelope.GroupId.IsNotEmpty())
        {
            request.MessageGroupId = envelope.GroupId;
        }

        if (envelope.DeduplicationId.IsNotEmpty())
        {
            request.MessageDeduplicationId = envelope.DeduplicationId;
        }

        foreach (var attribute in Mapper!.ToAttributes(envelope))
        {
            request.MessageAttributes ??= new();
            request.MessageAttributes.Add(attribute.Key, attribute.Value);
        }
        
        await Parent.SnsClient!.PublishAsync(request);
    }
    
    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        // TODO there is no "listening" to SNS topics, so not sure what to do this this one. Maybe Endpoint is the wrong class to use here?
        throw new NotSupportedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        Mapper ??= BuildMapper(runtime);
        
        if (Mode == EndpointMode.Inline)
        {
            return new InlineSnsSender(runtime, this);
        }
        
        var protocol = new SnsSenderProtocol(runtime, this,
            Parent.SnsClient ?? throw new InvalidOperationException("Parent transport has not been initialized"));
        return new BatchedSender(this, protocol, runtime.Cancellation,
            runtime.LoggerFactory.CreateLogger<SnsSenderProtocol>());
    }

    public override  async ValueTask InitializeAsync(ILogger logger)
    {
        if (_initialized)
        {
            return;
        }
        
        try
        {
            var client = Parent.SnsClient;

            if (client == null)
            {
                throw new InvalidOperationException($"Parent {nameof(AmazonSnsTransport)} has not been initialized");
            }

            if (Parent.AutoProvision)
            {
                await setupAsync(client);
                logger.LogInformation("Tried to create Amazon SNS topic {Name} if missing", TopicName);
                
                if (TopicSubscriptions.Any()) await createTopicSubscriptionsAsync(client);
            }
            
            await loadTopicArnIfEmptyAsync(client);
            await loadTopicSubscriptionsAsync(client);
        }
        catch (Exception e)
        {
            throw new WolverineSnsTransportException($"Error while trying to initialize Amazon SNS topic '{TopicName}'",
                e);
        }
        
        _initialized = true;
    }
    
    private async Task setupAsync(IAmazonSimpleNotificationService client)
    {
        Configuration.Name = TopicName;
        try
        {
            var response = await client.CreateTopicAsync(Configuration);

            TopicArn = response.TopicArn;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }
    
    private async Task loadTopicArnIfEmptyAsync(IAmazonSimpleNotificationService client)
    {
        if (TopicArn.IsEmpty())
        {
            var response = await client.FindTopicAsync(TopicName);

            if (response == null)
            {
                throw new NullReferenceException($"Could not find Amazon SNS topic '{TopicName}'");
            }
            
            TopicArn = response.TopicArn;
        }
    }

    private async Task loadTopicSubscriptionsAsync(IAmazonSimpleNotificationService client)
    {
        // If you have more than 100 subscriptions on one topic this code breaks
        var subscriptionResponse = await client.ListSubscriptionsByTopicAsync(TopicArn);
        foreach (var subscription in (subscriptionResponse?.Subscriptions ?? []).Where(x =>
                     TopicSubscriptions.FirstOrDefault(y => y.SubscriptionArn == x.SubscriptionArn) is null))
        {
            TopicSubscriptions.Add(new AmazonSnsSubscription(subscription));
        }
    }

    private async Task createTopicSubscriptionsAsync(IAmazonSimpleNotificationService client)
    {
        var sqsClient = Parent.SqsClient!;
                
        foreach (var subscription in TopicSubscriptions)
        {
            string endpoint;

            switch (subscription.Type)
            {
                case AmazonSnsSubscriptionType.Sqs:
                    var getQueueResponse = await sqsClient.GetQueueUrlAsync(subscription.Endpoint);
                    endpoint = await getSqsSubscriptionEndpointAsync(sqsClient, getQueueResponse.QueueUrl);
                    
                    await setQueuePolicyForTopic(sqsClient, new (getQueueResponse.QueueUrl, endpoint,  TopicArn));
                    break;
                default:
                    throw new NotImplementedException("AmazonSnsSubscriptionType not implemented");
            }

            var subscribeRequest = new SubscribeRequest(TopicArn, subscription.Protocol, endpoint);
            subscribeRequest.Attributes ??= new();
            subscribeRequest.Attributes[nameof(AmazonSnsSubscriptionAttributes.RawMessageDelivery)] =
                subscription.Attributes.RawMessageDelivery.ToString();

            if (subscription.Attributes.FilterPolicy.IsNotEmpty())
            {
                subscribeRequest.Attributes[nameof(AmazonSnsSubscriptionAttributes.FilterPolicy)] =
                    subscription.Attributes.FilterPolicy;
            }
            
            if (subscription.Attributes.RedrivePolicy.IsNotEmpty())
            {
                subscribeRequest.Attributes[nameof(AmazonSnsSubscriptionAttributes.RedrivePolicy)] =
                    subscription.Attributes.RedrivePolicy;
            }
            
            var subscribeResponse = await client.SubscribeAsync(subscribeRequest);
            subscription.SubscriptionArn = subscribeResponse.SubscriptionArn;
        }
    }

    private async Task<string> getSqsSubscriptionEndpointAsync(IAmazonSQS client, string queueUrl)
    {
        var queueAttributesRequest = new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = [QueueAttributeName.QueueArn]
        };
        
        var getAttributesResponse = await client.GetQueueAttributesAsync(queueAttributesRequest);
        return getAttributesResponse.QueueARN;
    }

    private async Task setQueuePolicyForTopic(IAmazonSQS client, SqsTopicDescription description)
    {
        var queuePolicy = Parent.QueuePolicyBuilder(description);

        await client.SetQueueAttributesAsync(
            new SetQueueAttributesRequest
            {
                QueueUrl = description.QueueUrl,
                Attributes = new Dictionary<string, string> { {"Policy", queuePolicy } }
            });
    }
}