using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports.SharedMemory;

public class SharedMemoryTransport : TransportBase<SharedMemoryEndpoint>
{
    public static readonly string ProtocolName = "shared-memory";

    public Cache<string, SharedMemoryTopic> Topics = new (topicName => new SharedMemoryTopic(topicName));
    private string _responseTopic;
    private SharedMemoryTopic _replyEndpoint;

    public SharedMemoryTransport() : base(ProtocolName, "Shared Memory Queues")
    {
        _responseTopic = Guid.NewGuid().ToString();
        var topic = Topics[_responseTopic];
        var subscription = new SharedMemorySubscription(topic, _responseTopic, EndpointRole.System);
        topic.TopicSubscriptions[_responseTopic] = subscription;

        ControlEndpoint = topic;
        _replyEndpoint = topic;
    }

    public SharedMemoryEndpoint ControlEndpoint { get; private set; }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var x in endpoints())
        {
            x.ReplyUri = _replyEndpoint.Uri;
        }
        
        return new ValueTask();
    }

    public override Endpoint? ReplyEndpoint()
    {
        return _replyEndpoint;
    }

    protected override IEnumerable<SharedMemoryEndpoint> endpoints()
    {
        foreach (var topic in Topics)
        {
            yield return topic;

            foreach (var subscription in topic.TopicSubscriptions)
            {
                yield return subscription;
            }
        }
    }

    protected override SharedMemoryEndpoint findEndpointByUri(Uri uri)
    {
        var topicName = uri.Host;
        var topic = Topics[topicName];
        if (uri.Segments.Any())
        {
            var subscriptionName = uri.Segments.Where(x => x != "/").LastOrDefault();

            if (subscriptionName.IsNotEmpty())
            {
                return topic.TopicSubscriptions[subscriptionName];
            }
        }

        return topic;
    }
}