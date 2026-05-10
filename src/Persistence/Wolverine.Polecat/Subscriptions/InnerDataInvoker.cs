using JasperFx.Events;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.Polecat.Subscriptions;

internal class InnerDataInvoker<T> : IMessageInvoker where T : notnull
{
    private readonly IMessageInvoker _inner;

    public InnerDataInvoker(IMessageInvoker inner)
    {
        _inner = inner;
    }

    public Task<T1> InvokeAsync<T1>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null,
        DeliveryOptions? options = null)
    {
        throw new NotSupportedException();
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null,
        DeliveryOptions? options = null)
    {
        if (message is IEvent<T> e)
        {
            return _inner.InvokeAsync(e.Data, bus, cancellation, timeout, options);
        }

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<T1> StreamAsync<T1>(object message, MessageBus bus,
        CancellationToken cancellation = default, DeliveryOptions? options = null)
        => throw new NotSupportedException();
}
