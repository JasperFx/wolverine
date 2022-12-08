using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Runtime.ResponseReply;

namespace Wolverine;

/// <summary>
/// Send or invoke messages to a specific endpoint
/// </summary>
public interface IDestinationEndpoint
{
    Uri Uri { get; }
    string EndpointName { get; }
    
    /// <summary>
    ///     Sends a message to this destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    ValueTask SendAsync<T>(T message, DeliveryOptions? options = null);
    
    /// <summary>
    /// Execute the message at this destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    Task<Acknowledgement> InvokeAsync(object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null);
    
    /// <summary>
    /// Execute the summary at this destination and retrieve the expected
    /// response from the destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        where T : class;
}



/// <summary>
/// Entry point for processing or publishing messages with Wolverine
/// </summary>
public interface IMessageBus : ICommandBus
{
    /// <summary>
    /// Publish or process messages at a specific endpoint by
    /// endpoint name
    /// </summary>
    /// <param name="endpointName"></param>
    /// <returns></returns>
    IDestinationEndpoint EndpointFor(string endpointName);
    
    /// <summary>
    /// Publish or process messages at a specific endpoint
    /// by the endpoint's Uri
    /// </summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    IDestinationEndpoint EndpointFor(Uri uri);
    
    
    
    /// <summary>
    /// Preview how Wolverine where and how this message would be sent. Use this as a debugging tool.
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    IReadOnlyList<Envelope> PreviewSubscriptions(object message);
    
    /// <summary>
    ///     Sends a message to the expected, one subscriber. Will throw an exception if there are no known subscribers
    /// </summary>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    ValueTask SendAsync<T>(T message, DeliveryOptions? options = null);

    /// <summary>
    ///     Publish a message to all known subscribers. Ignores the message if there are no known subscribers
    /// </summary>
    /// <param name="message"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null);

    /// <summary>
    ///     Send a message to a specific topic name. This relies
    ///     on having a backing transport endpoint that supports
    ///     topic routing
    /// </summary>
    /// <param name="topicName"></param>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    ValueTask SendToTopicAsync(string topicName, object message, DeliveryOptions? options = null);

    /// <summary>
    ///     Send a message to a specific, named endpoint
    /// </summary>
    /// <param name="endpointName"></param>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    ValueTask SendToEndpointAsync(string endpointName, object message, DeliveryOptions? options = null);

    /// <summary>
    ///     Send to a specific destination rather than running the routing rules
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="destination">The destination to send to</param>
    /// <param name="message"></param>
    ValueTask SendAsync<T>(Uri destination, T message, DeliveryOptions? options = null);

    /// <summary>
    ///     Send a message that should be executed at the given time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    [Obsolete]
    ValueTask SchedulePublishAsync<T>(T message, DateTimeOffset time, DeliveryOptions? options = null);

    /// <summary>
    ///     Send a message that should be executed after the given delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    [Obsolete]
    ValueTask SchedulePublishAsync<T>(T message, TimeSpan delay, DeliveryOptions? options = null);

    [Obsolete]
    Task<Acknowledgement> SendAndWaitAsync(object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null);

    [Obsolete]
    Task<T> RequestAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        where T : class;

}