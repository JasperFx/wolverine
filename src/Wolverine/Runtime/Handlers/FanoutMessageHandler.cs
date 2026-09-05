namespace Wolverine.Runtime.Handlers;

internal class FanoutMessageHandler<T> : MessageHandler<T>
{
    private readonly Uri[] _localQueueUris;

    public FanoutMessageHandler(Uri[] localQueueUris, HandlerChain chain)
    {
        // GH-4332 / GH-2303: deduplicate ONCE here. The safety net against a sticky handler
        // queue reachable by multiple paths is a property of this fixed array, so recomputing it
        // per message (a HashSet<Uri> allocation plus a full-string Uri.GetHashCode per entry)
        // only ever produced the same answer.
        _localQueueUris = localQueueUris.Distinct().ToArray();
        Chain = chain;
    }

    protected override async Task HandleAsync(T message, MessageContext context, CancellationToken cancellation)
    {
        var incoming = context.Envelope!;
        DeliveryOptions? options = null;
        if (incoming.HasHeaders)
        {
            options = new DeliveryOptions();
            foreach (var header in incoming.Headers)
            {
                options.WithHeader(header.Key, header.Value ?? string.Empty);
            }
        }

        // _localQueueUris is already distinct -- see the constructor
        foreach (var uri in _localQueueUris)
        {
            await context.EndpointFor(uri).SendAsync(message, options);
        }
    }
}
