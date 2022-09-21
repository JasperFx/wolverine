using System.Threading;
using System.Threading.Tasks;
using Baseline;

namespace Wolverine.Runtime.Handlers;

internal class ForwardingHandler<T, TDestination> : MessageHandler where T : IForwardsTo<TDestination>
{
    private readonly HandlerGraph _graph;

    public ForwardingHandler(HandlerGraph graph)
    {
        _graph = graph;
        Chain = new HandlerChain(typeof(T), graph);
    }

    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var innerMessage = context.Envelope!.Message!.As<T>();
        context.Envelope.Message = innerMessage.Transform();

        // TODO -- this should be memoized
        var inner = _graph.HandlerFor(typeof(TDestination));

        return inner!.HandleAsync(context, cancellation);
    }
}
