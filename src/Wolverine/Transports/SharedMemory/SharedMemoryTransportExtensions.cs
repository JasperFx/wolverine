using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Transports.SharedMemory;

public static class SharedMemoryTransportExtensions
{
    /// <summary>
    /// Create a subscription rule that publishes matching messages to the SignalR Hub of type "T"
    /// </summary>
    /// <param name="publishing"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static SharedMemorySubscriberConfiguration ToSharedMemoryTopic(this IPublishToExpression publishing, string topicName) 
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<SharedMemoryTransport>();

        var topic = transport.Topics[topicName];
        
        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new SharedMemorySubscriberConfiguration(topic);
    }
    
    /// <summary>
    ///     Listen for incoming messages at the designated Rabbit MQ queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName">The name of the Rabbit MQ queue</param>
    /// <param name="configure">
    ///     Optional configuration for this Rabbit Mq queue if being initialized by Wolverine
    ///     <returns></returns>
    public static SharedMemoryListenerConfiguration ListenToSharedMemorySubscription(this WolverineOptions endpoints, string topicName, string subscriptionName)
    {
        var transport = endpoints.Transports.GetOrCreate<SharedMemoryTransport>();
        var topic = transport.Topics[topicName];
        var subscription = topic.TopicSubscriptions[subscriptionName];

        return new SharedMemoryListenerConfiguration(subscription);
    }
}

public class SharedMemoryListenerConfiguration : ListenerConfiguration<SharedMemoryListenerConfiguration,
    SharedMemorySubscription>
{
    public SharedMemoryListenerConfiguration(SharedMemorySubscription endpoint) : base(endpoint)
    {
    }
}

public class SharedMemorySubscriberConfiguration : SubscriberConfiguration<SharedMemorySubscriberConfiguration,
    SharedMemoryTopic>
{
    public SharedMemorySubscriberConfiguration(SharedMemoryTopic endpoint) : base(endpoint)
    {
    }
}