using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.Marten.Subscriptions;

internal class NulloMessageInvoker : IMessageInvoker
{
    public Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null,
        DeliveryOptions? options = null)
    {
        throw new NotSupportedException();
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null,
        DeliveryOptions? options = null)
    {
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<T> StreamAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default, DeliveryOptions? options = null)
        => throw new NotSupportedException();
}