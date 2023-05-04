using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.Agents;

internal class InternalMessageHandler<T> : MessageHandler
{
    private readonly IInternalHandler<T> _handler;

    public InternalMessageHandler(IInternalHandler<T> handler)
    {
        _handler = handler;
    }

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var message = (T)context.Envelope!.Message!;

        await foreach (var outgoing in _handler.HandleAsync(message).WithCancellation(cancellation))
        {
            await context.EnqueueCascadingAsync(outgoing);
        }
    }
}

internal interface IInternalHandler<T>
{
    IAsyncEnumerable<object> HandleAsync(T message);
}