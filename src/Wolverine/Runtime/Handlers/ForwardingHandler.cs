using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

internal class ForwardingHandler<T, TDestination> : MessageHandler where T : IForwardsTo<TDestination>
{
    private readonly Lazy<IMessageHandler> _inner;

    public ForwardingHandler(HandlerGraph graph)
    {
        Chain = new HandlerChain(typeof(T), graph);

        _inner = new Lazy<IMessageHandler>(() => graph.HandlerFor(typeof(TDestination))!);
    }

    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var message = context.Envelope!.Message;

        // GH-4743. Transform only while the SOURCE message is still on the envelope. Writing the
        // transformed message back is what the inner handler reads, but nothing ever restores it -- and an
        // inline retry (Executor.InvokeInlineAsync loops on the same context and the same envelope) came
        // back in here with TDestination already in place. The old unconditional As<T>() then threw an
        // InvalidCastException, which matches no retry policy, so the caller of InvokeAsync saw a cast
        // error in place of either success or the original transient failure.
        if (message is T source)
        {
            context.Envelope.Message = source.Transform();
        }
        else if (message is not TDestination)
        {
            throw new InvalidCastException(
                $"Expected a {typeof(T).FullNameInCode()} or an already-forwarded {typeof(TDestination).FullNameInCode()}, but the envelope carries {message?.GetType().FullNameInCode() ?? "null"}");
        }

        return _inner.Value.HandleAsync(context, cancellation);
    }
}