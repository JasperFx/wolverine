using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSnsTopic : AmazonEndpoint
{
    private bool _initialized;
    
    private ISnsEnvelopeMapper _mapper = new DefaultSnsEnvelopeMapper();
    
    internal AmazonSnsTopic(string topicName, AmazonSqsTransport parent) 
        : base(topicName, parent, new Uri($"{AmazonSqsTransport.SqsProtocol}://{AmazonSqsTransport.SnsSegment}/{topicName}"))
    {
        TopicName = topicName;

        Configuration = new CreateTopicRequest(TopicName);

        // MessageBatchSize = 10;
    }
    
    /// <summary>
    ///     Pluggable strategy for interoperability with non-Wolverine systems. Customizes how the incoming SNS requests
    ///     are read and how outgoing messages are written to SNS
    /// </summary>
    /// <exception cref="ArgumentNullException"></exception>
    public ISnsEnvelopeMapper Mapper
    {
        get => _mapper;
        set => _mapper = value ?? throw new ArgumentNullException(nameof(value));
    }
    
    public string TopicName { get; }
    
    public CreateTopicRequest Configuration { get; }
    
    public ICollection<SubscribeRequest> TopicSubscriptions { get; } = new List<SubscribeRequest>();
    
    public override ValueTask<bool> CheckAsync()
    {
        throw new NotImplementedException();
    }

    public override async ValueTask TeardownAsync(ILogger logger)
    {
        await Parent.SnsClient!.DeleteTopicAsync(new DeleteTopicRequest(EndpointArn));
    }

    public override ValueTask SetupAsync(ILogger logger)
    {
        return new ValueTask(SetupAsync(Parent.SnsClient!));
    }
    
    public override ValueTask PurgeAsync(ILogger logger)
    {
        // TODO We can't really purge SNS topics, so probably do nothing here
        return new ValueTask(Task.CompletedTask);
    }
    
    public override async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var client = Parent.SnsClient!;

        if (EndpointArn.IsEmpty())
        {
            await SetupAsync(client);
        }
        
        var atts = await client.GetTopicAttributesAsync(EndpointArn);
        atts.Attributes.Add("name", TopicName);
        
        // TODO return all attributes?
        return atts.Attributes;
    }
    
    internal override async Task SendMessageAsync(Envelope envelope, ILogger logger)
    {
        if (!_initialized)
        {
            await InitializeAsync(logger);
        }
        
        var body = _mapper.BuildMessageBody(envelope);
        var request = new PublishRequest(EndpointArn, body);
        if (envelope.GroupId.IsNotEmpty())
        {
            request.MessageGroupId = envelope.GroupId;
        }

        if (envelope.DeduplicationId.IsNotEmpty())
        {
            request.MessageDeduplicationId = envelope.DeduplicationId;
        }

        foreach (var attribute in _mapper.ToAttributes(envelope))
        {
            request.MessageAttributes.Add(attribute.Key, attribute.Value);
        }
        
        await Parent.SnsClient!.PublishAsync(request);
    }
    
    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        // TODO there is no "listening" to SNS topics, so not sure what to do this this one. Maybe Endpoint is the wrong class to use here?
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new InlineAmazonSender(runtime, this);
    }

    public override async ValueTask InitializeAsync(ILogger logger)
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
                throw new InvalidOperationException($"Parent {nameof(AmazonSqsTransport)} has not been initialized");
            }
            
            await SetupAsync(client);
            
            if (Parent.AutoProvision)
            {
                foreach (var subscribeRequest in TopicSubscriptions)
                {
                    await client.SubscribeAsync(subscribeRequest);
                }
            }
        }
        catch (Exception e)
        {
            throw new WolverineSqsTransportException($"Error while trying to initialize Amazon SNS topic '{TopicName}'",
                e);
        }
        
        _initialized = true;
    }
    
    private async Task SetupAsync(IAmazonSimpleNotificationService client)
    {
        try
        {
            var response = await client.CreateTopicAsync(Configuration);

            EndpointArn = response.TopicArn;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }
}
