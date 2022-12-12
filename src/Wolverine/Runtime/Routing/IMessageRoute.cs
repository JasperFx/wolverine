using System;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

public interface IMessageRoute
{
    Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime);

    public Task<T> InvokeAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null);
}