namespace Wolverine.Runtime.Handlers;

internal class FanoutMessageHandler<T> : MessageHandler<T>
{
    private readonly Uri[] _localQueueUris;

    public FanoutMessageHandler(Uri[] localQueueUris, HandlerChain chain)
    {
        _localQueueUris = localQueueUris;
        Chain = chain;
    }

    protected override async Task HandleAsync(T message, MessageContext context, CancellationToken cancellation)
    {
        var incoming = context.Envelope!;
        DeliveryOptions? options = null;
        if (incoming.Headers.Count > 0)
        {
            options = new DeliveryOptions();
            foreach (var header in incoming.Headers)
            {
                options.WithHeader(header.Key, header.Value ?? string.Empty);
            }
        }

        // Safety net: deduplicate target URIs to prevent double delivery
        // when the same sticky handler queue is reachable via multiple paths.
        // See https://github.com/JasperFx/wolverine/issues/2303
        var sent = new HashSet<Uri>();
        foreach (var uri in _localQueueUris)
        {
            if (sent.Add(uri))
            {
                await context.EndpointFor(uri).SendAsync(message, options);
            }
        }
    }
}
