using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.SharedMemory;

public class SharedMemoryTopic : SharedMemoryEndpoint, ISender
{
    private Topic _topic;
    public string TopicName { get; }
    
    public Cache<string, SharedMemorySubscription> TopicSubscriptions { get; }

    public SharedMemoryTopic(string topicName) : this(topicName, EndpointRole.Application)
    {
        
    }

    public SharedMemoryTopic(string topicName, EndpointRole role) : base(new Uri($"{SharedMemoryTransport.ProtocolName}://{topicName}"), role)
    {
        TopicName = topicName;
        TopicSubscriptions = new(name => new(this, name, EndpointRole.Application));
        if (role == EndpointRole.System)
        {
            TopicSubscriptions[topicName] = new SharedMemorySubscription(this, topicName, EndpointRole.System);
        }

        // Placeholder
        ReplyUri = Uri;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        _topic = SharedMemoryQueueManager.Topics[TopicName];
        return this;
    }

    bool ISender.SupportsNativeScheduledSend => false;
    Uri ISender.Destination => Uri;

    Task<bool> ISender.PingAsync()
    {
        return Task.FromResult(true);
    }

    public Uri ReplyUri { get; set; }

    public ValueTask SendAsync(Envelope envelope)
    {
        envelope.ReplyUri = ReplyUri;
        return _topic.PostAsync(envelope);
    }
}