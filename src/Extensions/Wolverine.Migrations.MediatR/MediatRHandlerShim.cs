using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Shims.MediatR;

/// <summary>
/// Shim that wraps a MediatR IRequestHandler&lt;TRequest, TResponse&gt;
/// to make it work as a Wolverine message handler.
/// </summary>
/// <typeparam name="TRequest">The request type</typeparam>
/// <typeparam name="TResponse">The response type</typeparam>
public class MediatRHandlerShim<TRequest, TResponse> : MessageHandler<TRequest>
    where TRequest : IRequest<TResponse>
{
    protected override async Task HandleAsync(TRequest message, MessageContext context, CancellationToken cancellation)
    {
        // Resolve the MediatR handler from the service provider
        var handlerType = typeof(IRequestHandler<TRequest, TResponse>);
        var handler = context.Runtime.Services.GetRequiredService(handlerType);

        if (handler == null)
        {
            return;
        }

        // Call the handler - standard MediatR signature: Handle(TRequest, CancellationToken)
        var handleMethod = handlerType.GetMethod(nameof(IRequestHandler<TRequest, TResponse>.Handle));

        if (handleMethod == null)
        {
            return;
        }

        var task = (Task)handleMethod.Invoke(handler, [message, cancellation])!;
        await task.ConfigureAwait(false);

        // Get the result from the completed task
        var resultProperty = task.GetType().GetProperty("Result");
        var result = resultProperty?.GetValue(task);

        if (result != null)
        {
            await context.EnqueueCascadingAsync(result);
        }
    }
}
