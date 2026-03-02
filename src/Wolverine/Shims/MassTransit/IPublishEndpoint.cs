namespace Wolverine.Shims.MassTransit;

/// <summary>
/// MassTransit-compatible interface for publishing event messages.
/// Delegates to Wolverine's <see cref="IMessageBus.PublishAsync{T}"/>.
/// </summary>
public interface IPublishEndpoint
{
    /// <summary>
    /// Publishes an event message to all subscribers.
    /// </summary>
    Task Publish<T>(T message) where T : class;
}
