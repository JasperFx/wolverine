namespace Wolverine.Runtime.Routing;

public interface IMessageInvoker
{
    Task<T> InvokeAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null);

    Task InvokeAsync(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null);

    /// <summary>
    /// Execute the handler for this message and stream back a typed sequence of response objects.
    /// The handler must return <see cref="IAsyncEnumerable{T}"/> of the response type.
    /// Only supported for locally-handled messages.
    /// </summary>
    IAsyncEnumerable<T> StreamAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        DeliveryOptions? options = null);
}