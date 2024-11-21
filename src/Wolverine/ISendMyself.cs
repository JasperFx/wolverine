using JasperFx.Core;
using Wolverine.Configuration;

namespace Wolverine;

/// <summary>
///     Interface for cascading messages that require some customization of how
///     the resulting inner message is sent out
/// </summary>
public interface ISendMyself : IWolverineReturnType
{
    ValueTask ApplyAsync(IMessageContext context);
}

/// <summary>
///     DelayedMessage specifically for saga timeouts. Inheriting from TimeoutMessage
///     tells Wolverine that this message is to enforce saga timeouts and can be ignored
///     if the underlying saga does not exist
/// </summary>
public abstract record TimeoutMessage(TimeSpan DelayTime) : ISendMyself
{
    public virtual ValueTask ApplyAsync(IMessageContext context)
    {
        return context.ScheduleAsync(this, DelayTime);
    }
}

public static class ConfiguredMessageExtensions
{
    /// <summary>
    /// Create a cascading message tagged to a specific tenant id
    /// </summary>
    /// <param name="message"></param>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    public static DeliveryMessage<T> WithTenantId<T>(this T message, string tenantId)
    {
        return new DeliveryMessage<T>(message, new DeliveryOptions { TenantId = tenantId });
    }
    
    /// <summary>
    /// Create a cascading message tagged to a specific tenant id
    /// </summary>
    /// <param name="message"></param>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    public static DeliveryMessage<T> WithTenantId<T>(this DeliveryMessage<T> message, string tenantId)
    {
        message.Options.TenantId = tenantId;
        return message;
    }
    
    /// <summary>
    /// Create a cascading message tagged to a specific group id
    /// </summary>
    /// <param name="message"></param>
    /// <param name="groupId"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static DeliveryMessage<T> WithGroupId<T>(this T message, string groupId)
    {
        return new DeliveryMessage<T>(message, new DeliveryOptions { GroupId = groupId });
    }
    
    /// <summary>
    /// Create a cascading message tagged to a specific group id
    /// </summary>
    /// <param name="message"></param>
    /// <param name="groupId"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static DeliveryMessage<T> WithGroupId<T>(this DeliveryMessage<T> message, string groupId)
    {
        message.Options.GroupId = groupId;
        return message;
    }

    /// <summary>
    /// Create a cascading message tagged to a specific group id and scheduled for a set time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="groupId"></param>
    /// <param name="scheduledTime"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static DeliveryMessage<T> ScheduleToGroup<T>(this T message, string groupId, DateTimeOffset scheduledTime)
    {
        return new DeliveryMessage<T>(message, new DeliveryOptions { GroupId = groupId, ScheduledTime = scheduledTime});
    }

    /// <summary>
    /// Create a cascading message tagged to a specific group id and scheduled with a delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="groupId"></param>
    /// <param name="scheduleDelay"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static DeliveryMessage<T> ScheduleToGroup<T>(this T message, string groupId, TimeSpan scheduleDelay)
    {
        return new DeliveryMessage<T>(message, new DeliveryOptions { GroupId = groupId, ScheduleDelay = scheduleDelay});
    }

    /// <summary>
    ///     Send the current object as a cascading message with explicit
    ///     delivery options
    /// </summary>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static DeliveryMessage<T> WithDeliveryOptions<T>(this T message, DeliveryOptions options)
    {
        return new DeliveryMessage<T>(message, options);
    }

    /// <summary>
    ///     Schedule the inner outgoing message to be sent at the specified time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <returns></returns>
    public static ScheduledMessage<T> ScheduledAt<T>(this T message, DateTimeOffset time)
    {
        return new ScheduledMessage<T>(message, time);
    }
    
    /// <summary>
    ///     Schedule the inner outgoing message to be sent at the specified time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <returns></returns>
    public static DeliveryMessage<T> ScheduledAt<T>(this DeliveryMessage<T> message, DateTimeOffset time)
    {
        message.Options.ScheduledTime = time;
        return message;
    }

    /// <summary>
    ///     Schedule the inner outgoing message to be sent after the specified delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <returns></returns>
    public static DeliveryMessage<T> DelayedFor<T>(this T message, TimeSpan delay)
    {
        return new DeliveryMessage<T>(message, new DeliveryOptions{ScheduleDelay = delay});
    }
    
    /// <summary>
    ///     Schedule the inner outgoing message to be sent after the specified delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <returns></returns>
    public static DeliveryMessage<T> DelayedFor<T>(this DeliveryMessage<T> message, TimeSpan delay)
    {
        message.Options.ScheduleDelay = delay;
        return message;
    }

    /// <summary>
    /// Send a message directly to the named endpoint as a cascading message
    /// </summary>
    /// <param name="message"></param>
    /// <param name="endpointName"></param>
    /// <returns></returns>
    public static RoutedToEndpointMessage<T> ToEndpoint<T>(this T message, string endpointName, DeliveryOptions? options = null)
    {
        return new RoutedToEndpointMessage<T>(endpointName, message, options);
    }
    
    /// <summary>
    /// Send a message directly to the named endpoint as a cascading message
    /// </summary>
    /// <param name="message"></param>
    /// <param name="endpointName"></param>
    /// <returns></returns>
    public static RoutedToEndpointMessage<T> ToEndpoint<T>(this DeliveryMessage<T> message, string endpointName)
    {
        return new RoutedToEndpointMessage<T>(endpointName, message.Message, message.Options);
    }
    
    /// <summary>
    /// Send a message directly to the specific destination as a cascading message
    /// </summary>
    /// <param name="message"></param>
    /// <param name="destination"></param>
    /// <returns></returns>
    public static RoutedToEndpointMessage<T> ToDestination<T>(this T message, Uri destination, DeliveryOptions? options = null)
    {
        return new RoutedToEndpointMessage<T>(destination, message, options);
    }

    /// <summary>
    /// Send a message directly to the specific destination as a cascading message
    /// </summary>
    /// <param name="message"></param>
    /// <param name="destination"></param>
    /// <returns></returns>
    public static RoutedToEndpointMessage<T> ToDestination<T>(this DeliveryMessage<T> message, Uri destination)
    {
        return new RoutedToEndpointMessage<T>(destination, message.Message, message.Options);
    }

    /// <summary>
    /// Send a message to the supplied topic
    /// </summary>
    /// <param name="message"></param>
    /// <param name="topic">The topic name for the underlying message broker</param>
    /// <param name="options">Optional delivery options</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static TopicMessage<T> ToTopic<T>(this T message, string topic, DeliveryOptions? options = null)
    {
        return new TopicMessage<T>(message, topic, options);
    }
    
    /// <summary>
    /// Send a message to the supplied topic
    /// </summary>
    /// <param name="message"></param>
    /// <param name="topic">The topic name for the underlying message broker</param>
    /// <param name="options">Optional delivery options</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static TopicMessage<T> ToTopic<T>(this DeliveryMessage<T> message, string topic)
    {
        return new TopicMessage<T>(message.Message, topic, message.Options);
    }
}

public record TopicMessage<T>(T Message, string Topic, DeliveryOptions? Options) : ISendMyself
{
    ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        return context.BroadcastToTopicAsync(Topic, Message!, Options);
    }
}

public class ScheduledMessage<T> : DeliveryMessage<T>
{
    public ScheduledMessage(T message, DateTimeOffset time) : base(message,
        new DeliveryOptions { ScheduledTime = time })
    {
    }
}

public class DeliveryMessage<T> : ISendMyself
{
    public DeliveryMessage(T message, DeliveryOptions options)
    {
        Message = message;
        Options = options;
    }

    public T Message { get; }
    public DeliveryOptions Options { get; }

    ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        return context.PublishAsync(Message, Options);
    }
}

public class RoutedToEndpointMessage<T> : ISendMyself
{
    public string? EndpointName { get; set; }
    public Uri? Destination { get; set; }

    public T Message { get; }
    public DeliveryOptions? DeliveryOptions { get; }

    public RoutedToEndpointMessage(string endpointName, T message, DeliveryOptions? deliveryOptions = null)
    {
        if (endpointName == null)
        {
            throw new ArgumentNullException(nameof(endpointName));
        }

        EndpointName = endpointName ?? throw new ArgumentNullException(nameof(endpointName));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        DeliveryOptions = deliveryOptions;
    }

    public RoutedToEndpointMessage(Uri destination, T message, DeliveryOptions? deliveryOptions = null)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        DeliveryOptions = deliveryOptions;
    }

    ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        return EndpointName.IsNotEmpty()
            ? context.EndpointFor(EndpointName).SendAsync(Message, DeliveryOptions)
            : context.EndpointFor(Destination).SendAsync(Message, DeliveryOptions);
    }
}