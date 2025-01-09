namespace Wolverine.Runtime.Routing;

#region sample_IMessageRouter

/// <summary>
/// Holds the compiled rules for the message routing of a single message type
/// </summary>
public interface IMessageRouter
{
    /// <summary>
    /// All of the known, possible message routes (destination endpoint and any endpoint
    /// sending rules configured for that endpoint)
    /// </summary>
    IMessageRoute[] Routes { get; }
    
    /// <summary>
    /// Creates the outgoing envelopes for a message and optional delivery
    /// options for sending. May throw an exception if there are no routes
    /// </summary>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    Envelope[] RouteForSend(object message, DeliveryOptions? options);
    
    /// <summary>
    /// Creates zero to many outgoing envelopes for a message and optional delivery
    /// options for publishing
    /// </summary>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    Envelope[] RouteForPublish(object message, DeliveryOptions? options);
    
    /// <summary>
    /// Creates an outgoing envelope for a specific sending endpoint
    /// </summary>
    /// <param name="message"></param>
    /// <param name="uri"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    Envelope RouteToDestination(object message, Uri uri, DeliveryOptions? options);
    
    /// <summary>
    /// Creates 0 to many outgoing envelopes for the supplied message, topic name, and optional
    /// delivery options. This will only apply for subscriptions that accept topics as part
    /// of sending like a Rabbit MQ topic exchange or topic-centric brokers like Kafka or MQTT
    /// </summary>
    /// <param name="message"></param>
    /// <param name="topicName"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    Envelope[] RouteToTopic(object message, string topicName, DeliveryOptions? options);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IMessageRoute FindSingleRouteForSending();
}

#endregion