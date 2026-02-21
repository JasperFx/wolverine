using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Shims.MediatR;

/// <summary>
/// Shim that wraps a MediatR IRequestHandler&lt;TRequest&gt; (void response)
/// to make it work as a Wolverine message handler.
/// </summary>
/// <typeparam name="TRequest">The request type</typeparam>
public class MediatRHandlerShim<TRequest> : MessageHandler<TRequest>
    where TRequest : IRequest
{
    protected override async Task HandleAsync(TRequest message, MessageContext context, CancellationToken cancellation)
    {
        // Resolve the MediatR handler from the service provider
        var handlerType = typeof(IRequestHandler<TRequest>);
        var handler = context.Runtime.Services.GetRequiredService(handlerType);

        if (handler == null)
        {
            return;
        }

        // Call the handler - standard MediatR signature: Handle(TRequest, CancellationToken)
        var handleMethod = handlerType.GetMethod(nameof(IRequestHandler<TRequest>.Handle));

        if (handleMethod == null)
        {
            return;
        }

        await (Task)handleMethod.Invoke(handler, [message, cancellation])!;
    }
}
