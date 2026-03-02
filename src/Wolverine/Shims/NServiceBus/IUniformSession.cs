namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// NServiceBus-compatible unified interface for sending and publishing that works
/// both inside and outside of message handlers.
/// Delegates to Wolverine's <see cref="IMessageBus"/>.
/// </summary>
public interface IUniformSession
{
    /// <summary>
    /// Sends a command message.
    /// Maps to <see cref="IMessageBus.SendAsync{T}"/>.
    /// </summary>
    Task Send(object message, SendOptions? options = null);

    /// <summary>
    /// Publishes an event message.
    /// Maps to <see cref="IMessageBus.PublishAsync{T}"/>.
    /// </summary>
    Task Publish(object message, PublishOptions? options = null);
}
