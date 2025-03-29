using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
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
    private readonly AmazonSnsTransport _parent;
    
    private bool _initialized;
    
    private ISnsEnvelopeMapper _mapper = new DefaultSnsEnvelopeMapper();
    
    internal AmazonSnsTopic(string topicName, AmazonSnsTransport parent) 
        : base(new Uri($"{AmazonSnsTransport.SnsProtocol}://{topicName}"), EndpointRole.Application)
    {
        _parent = parent;
        
        TopicName = topicName;

        Configuration = new CreateTopicRequest(TopicName);
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
    public string TopicArn { get; set; }
    
    public CreateTopicRequest Configuration { get; }
    
    public ValueTask<bool> CheckAsync()
    {
        throw new NotImplementedException();
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        await _parent.Client!.DeleteTopicAsync(new DeleteTopicRequest(TopicArn));
    }

    public ValueTask SetupAsync(ILogger logger)
    {
        return new ValueTask(SetupAsync(_parent.Client!));
    }
    
    public ValueTask PurgeAsync(ILogger logger)
    {
        // TODO We can't really purge SNS topics, so probably do nothing here
        return new ValueTask(Task.CompletedTask);
    }
    
    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var client = _parent.Client!;

        if (TopicArn.IsEmpty())
        {
            await SetupAsync(client);
        }
        
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
        
        var body = _mapper.BuildMessageBody(envelope);
        var request = new PublishRequest(TopicArn, body);
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
        
        await _parent.Client!.PublishAsync(request);
    }
    
    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        // TODO there is no "listening" to SNS topics, so not sure what to do this this one. Maybe Endpoint is the wrong class to use here?
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new InlineSnsSender(runtime, this);
    }

    public override  async ValueTask InitializeAsync(ILogger logger)
    {
        if (_initialized)
        {
            return;
        }
        
        try
        {
            var client = _parent.Client;

            if (client == null)
            {
                throw new InvalidOperationException($"Parent {nameof(AmazonSnsTransport)} has not been initialized");
            }

            if (_parent.AutoProvision)
            {
                await SetupAsync(client);
                logger.LogInformation("Tried to create Amazon SNS topic {Name} if missing", TopicName);
            }
            
            await LoadTopicArnIfEmptyAsync(client);
        }
        catch (Exception e)
        {
            throw new WolverineSnsTransportException($"Error while trying to initialize Amazon SNS topic '{TopicName}'",
                e);
        }
        
        _initialized = true;
    }
    
    private async Task SetupAsync(IAmazonSimpleNotificationService client)
    {
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
    
    private async Task LoadTopicArnIfEmptyAsync(IAmazonSimpleNotificationService client)
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
}
