namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// NServiceBus-compatible message handler interface.
/// Implementing this interface marks the class for Wolverine's handler discovery
/// via <see cref="IWolverineHandler"/>.
/// </summary>
/// <typeparam name="T">The message type to handle</typeparam>
public interface IHandleMessages<in T> : IWolverineHandler
{
    /// <summary>
    /// Handles a message of type <typeparamref name="T"/>.
    /// </summary>
    /// <param name="message">The message to handle</param>
    /// <param name="context">The handler context providing access to message metadata and messaging operations</param>
    Task Handle(T message, IMessageHandlerContext context);
}
