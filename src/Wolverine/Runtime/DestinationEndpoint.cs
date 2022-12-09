using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime.ResponseReply;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;

namespace Wolverine.Runtime;

internal class DestinationEndpoint : IDestinationEndpoint
{
    private readonly Endpoint _endpoint;
    private readonly MessageBus _parent;

    public DestinationEndpoint(Endpoint endpoint, MessageBus parent)
    {
        _endpoint = endpoint;
        _parent = parent;

        // Hokey, but guarantees that the sending agent is active
        if (endpoint.Agent == null)
        {
            parent.Runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
        }
    }

    public Uri Uri => _endpoint.Uri;
    public string EndpointName => _endpoint.EndpointName;

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message == null || message.GetType() == typeof(Uri))
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = _endpoint.RouteFor(message.GetType(), _parent.Runtime);
        var envelope = new Envelope(message, _endpoint.Agent!);
        if (options != null && options.ContentType.IsNotEmpty() && options.ContentType != envelope.ContentType)
        {
            envelope.Serializer = _parent.Runtime.Options.FindSerializer(options.ContentType);
        }
        
        foreach (var rule in route.Rules) rule.Modify(envelope);

        // Delivery options win
        options?.Override(envelope);

        // adjust for local, scheduled send
        if (envelope.IsScheduledForLater(DateTimeOffset.Now) && !_endpoint.Agent!.SupportsNativeScheduledSend)
        {
            var localDurableQueue =
                _parent.Runtime.Endpoints.GetOrBuildSendingAgent(TransportConstants.DurableLocalUri);
            envelope = envelope.ForScheduledSend(localDurableQueue);
        }
        
        _parent.TrackEnvelopeCorrelation(envelope);

        return _parent.PersistOrSendAsync(envelope);
    }

    public Task<Acknowledgement> InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = _endpoint.RouteFor(message.GetType(), _parent.Runtime);
        return route.InvokeAsync<Acknowledgement>(message, _parent, cancellation, timeout);
    }

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) where T : class
    {
        _parent.Runtime.RegisterMessageType(typeof(T));
        
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = _endpoint.RouteFor(message.GetType(), _parent.Runtime);
        return route.InvokeAsync<T>(message, _parent, cancellation, timeout);
    }
}