using JasperFx.Events;
using Marten.Events;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.Marten.Subscriptions;

internal class InnerDataInvoker<T> : IMessageInvoker
{
    private readonly IMessageInvoker _inner;

    public InnerDataInvoker(IMessageInvoker inner)
    {
        _inner = inner;
    }

    public Task<T1> InvokeAsync<T1>(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null,
        string? tenantId = null)
    {
        throw new NotSupportedException();
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null,
        string? tenantId = null)
    {
        if (message is IEvent<T> e)
        {
            return _inner.InvokeAsync(e.Data, bus, cancellation, timeout, tenantId);
        }

        return Task.CompletedTask;
    }
}