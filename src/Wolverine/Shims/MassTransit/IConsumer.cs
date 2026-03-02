namespace Wolverine.Shims.MassTransit;

/// <summary>
/// MassTransit-compatible consumer interface.
/// Implementing this interface marks the class for Wolverine's handler discovery
/// via <see cref="IWolverineHandler"/>.
/// </summary>
/// <typeparam name="T">The message type to consume</typeparam>
public interface IConsumer<in T> : IWolverineHandler where T : class
{
    /// <summary>
    /// Consumes a message of type <typeparamref name="T"/>.
    /// </summary>
    /// <param name="context">The consume context providing access to the message and messaging operations</param>
    Task Consume(ConsumeContext<T> context);
}
