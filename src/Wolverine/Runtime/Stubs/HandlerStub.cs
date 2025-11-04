using ImTools;
using Wolverine.Configuration;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Stubs;

public abstract class HandlerStub<TRequest, TResponse> : IHandlerStub<TRequest>
{
    public abstract TResponse Handle(TRequest message, IMessageContext context);

    async Task IHandlerStub<TRequest>.HandleAsync(TRequest message, IMessageContext context, IServiceProvider services, CancellationToken cancellation)
    {
        var response = Handle(message, context);
        await context.PublishAsync(response);
    }
}