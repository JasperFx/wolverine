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

public class ConfiguredMessage : ISendMyself
{
    public object Message { get; }
    public DeliveryOptions Options { get; }

    public ConfiguredMessage(object message, DeliveryOptions options)
    {
        Message = message;
        Options = options;
    }

    ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        return context.PublishAsync(Message, Options);
    }
}