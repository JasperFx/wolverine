namespace Wolverine.Runtime.Routing;

public interface IMessageInvoker
{
    Task<T> InvokeAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null);

    Task InvokeAsync(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null);
}