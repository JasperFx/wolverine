using System;
using System.Threading.Tasks;

namespace Wolverine;

/// <summary>
/// Entry point for sending or publishing messages
/// </summary>
public interface IMessagePublisher : ICommandBus
{
    /// <summary>
    /// Sends a message to the expected, one subscriber. Will throw an exception if there are no known subscribers
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
    /// Send a message to a specific, named endpoint
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
    ValueTask SchedulePublishAsync<T>(T message, DateTimeOffset time, DeliveryOptions? options = null);

    /// <summary>
    ///     Send a message that should be executed after the given delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    ValueTask SchedulePublishAsync<T>(T message, TimeSpan delay, DeliveryOptions? options = null);
}
