namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// NServiceBus-compatible interface representing a running endpoint instance.
/// Extends <see cref="IMessageSession"/> with lifecycle management.
/// Delegates to Wolverine's <see cref="IMessageBus"/>.
/// </summary>
public interface IEndpointInstance : IMessageSession
{
    /// <summary>
    /// Stops the endpoint instance.
    /// Maps to stopping the underlying host.
    /// </summary>
    Task Stop();
}
