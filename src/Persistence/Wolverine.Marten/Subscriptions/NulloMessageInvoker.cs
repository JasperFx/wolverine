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

    public async IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, MessageBus bus,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        throw new NotSupportedException();

        // Unreachable, but needed to satisfy compiler for IAsyncEnumerable
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}