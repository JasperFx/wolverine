using System;
using System.Threading.Tasks;
using Wolverine.Runtime;

namespace Wolverine;

/// <summary>
///     Interface for cascading messages that require some customization of how
///     the resulting inner message is sent out
/// </summary>
public interface ISendMyself
{
    ValueTask ApplyAsync(IMessageContext context);
}

/// <summary>
///     Base class that will add a scheduled delay to any messages
///     of this type that are used as a cascaded message returned from
///     a message handler
/// </summary>
public abstract record DelayedMessage(TimeSpan DelayTime) : ISendMyself
{
    public virtual ValueTask ApplyAsync(IMessageContext context)
    {
        return context.ScheduleAsync(this, DelayTime);
    }
}

/// <summary>
///     DelayedMessage specifically for saga timeouts. Inheriting from TimeoutMessage
///     tells Wolverine that this message is to enforce saga timeouts and can be ignored
///     if the underlying saga does not exist
/// </summary>
public abstract record TimeoutMessage(TimeSpan DelayTime) : DelayedMessage(DelayTime);

public static class ConfiguredMessageExtensions
{
    /// <summary>
    /// Schedule the inner outgoing message to be sent at the specified time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <returns></returns>
    public static DeliveryMessage ScheduledAt(this object message, DateTimeOffset time)
    {
        return new DeliveryMessage(message, new DeliveryOptions { ScheduledTime = time });
    }

    /// <summary>
    /// Schedule the inner outgoing message to be sent after the specified delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <returns></returns>
    public static DeliveryMessage DelayedFor(this object message, TimeSpan delay)
    {
        return new DeliveryMessage(message, new DeliveryOptions { ScheduleDelay = delay });
    }
}

public class DeliveryMessage : ISendMyself
{
    public object Message { get; }
    public DeliveryOptions Options { get; }

    public DeliveryMessage(object message, DeliveryOptions options)
    {
        Message = message;
        Options = options;
    }

    ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        return context.PublishAsync(Message, Options);
    }
}