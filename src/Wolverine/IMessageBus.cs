namespace Wolverine;

public static class MessageBusExtensions
{
    /// <summary>
    ///     Schedule the publishing or execution of a message until a later time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public static ValueTask ScheduleAsync<T>(this IMessageBus bus, T message, DateTimeOffset time,
        DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        options ??= new DeliveryOptions();
        options.ScheduledTime = time;

        return bus.PublishAsync(message, options);
    }

    /// <summary>
    ///     Schedule the publishing or execution of a message until a later time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public static ValueTask ScheduleAsync<T>(this IMessageBus bus, T message, TimeSpan delay,
        DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        options ??= new DeliveryOptions();
        options.ScheduleDelay = delay;
        return bus.PublishAsync(message, options);
    }
}

public interface ICommandBus
{
    /// <summary>
    ///     Execute the message handling for this message *right now* and wait for the completion.
    ///     If the message is handled locally, this delegates immediately
    ///     If the message is handled remotely, the message is sent and the method waits for the response
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout">Optional timeout</param>
    /// <returns></returns>
    Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = default);

    /// <summary>
    ///     Execute the message handling for this message *right now* and wait for the completion and the designated response
    ///     type T.
    ///     If the message is handled locally, this delegates immediately
    ///     If the message is handled remotely, the message is sent and the method waits for the response
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout">Optional timeout</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = default);
}

/// <summary>
///     Entry point for processing or publishing messages with Wolverine
/// </summary>
public interface IMessageBus : ICommandBus
{
    string? TenantId { get; set; }


    ///     Execute the message handling for this message *right now* against the specified tenant id and wait for the completion.
    ///     If the message is handled locally, this delegates immediately
    ///     If the message is handled remotely, the message is sent and the method waits for the response
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout">Optional timeout</param>
    /// <returns></returns>
    Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = default);


    /// <summary>
    ///     Execute the message handling for this message *right now* against the specified tenant id and wait for the completion and the designated response
    ///     type T.
    ///     If the message is handled locally, this delegates immediately
    ///     If the message is handled remotely, the message is sent and the method waits for the response
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout">Optional timeout</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = default);

    /// <summary>
    ///     Publish or process messages at a specific endpoint by
    ///     endpoint name
    /// </summary>
    /// <param name="endpointName"></param>
    /// <returns></returns>
    IDestinationEndpoint EndpointFor(string endpointName);

    /// <summary>
    ///     Publish or process messages at a specific endpoint
    ///     by the endpoint's Uri
    /// </summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    IDestinationEndpoint EndpointFor(Uri uri);

    /// <summary>
    ///     Preview how Wolverine where and how this message would be sent. Use this as a debugging tool.
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
    ///     topic routing.
    ///     At this point, this feature pretty well only matters with Rabbit MQ topic exchanges!
    /// </summary>
    /// <param name="topicName"></param>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null);
}