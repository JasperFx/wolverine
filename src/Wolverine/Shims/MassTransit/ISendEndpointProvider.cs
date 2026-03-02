namespace Wolverine.Shims.MassTransit;

/// <summary>
/// MassTransit-compatible interface for sending command messages.
/// Delegates to Wolverine's <see cref="IMessageBus.SendAsync{T}"/>.
/// </summary>
public interface ISendEndpointProvider
{
    /// <summary>
    /// Sends a command message.
    /// </summary>
    Task Send<T>(T message) where T : class;

    /// <summary>
    /// Sends a command message to a specific endpoint.
    /// </summary>
    Task Send<T>(T message, Uri destinationAddress) where T : class;
}
