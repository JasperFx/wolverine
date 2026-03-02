namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// NServiceBus-compatible interface for sending and publishing messages outside of a message handler.
/// Delegates to Wolverine's <see cref="IMessageBus"/>.
/// </summary>
public interface IMessageSession
{
    /// <summary>
    /// Sends a command message. In NServiceBus, Send is for commands (point-to-point).
    /// Maps to <see cref="IMessageBus.SendAsync{T}"/>.
    /// </summary>
    Task Send(object message, SendOptions? options = null);

    /// <summary>
    /// Publishes an event message. In NServiceBus, Publish is for events (pub-sub).
    /// Maps to <see cref="IMessageBus.PublishAsync{T}"/>.
    /// </summary>
    Task Publish(object message, PublishOptions? options = null);
}
