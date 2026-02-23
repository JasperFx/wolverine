using System.Diagnostics;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

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
        if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow) && !_endpoint.Agent!.SupportsNativeScheduledSend)
        {
            var localDurableQueue =
                _parent.Runtime.Endpoints.GetOrBuildSendingAgent(TransportConstants.DurableLocalUri);
            envelope = envelope.ForScheduledSend(localDurableQueue);
        }

        _parent.TrackEnvelopeCorrelation(envelope, Activity.Current);

        return _parent.PersistOrSendAsync(envelope);
    }

    public ValueTask SendRawMessageAsync(byte[] data, Type? messageType = null, Action<Envelope>? configure = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (data.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Zero length data is not valid for this usage");
        }

        var envelope = new Envelope
        {
            Data = data,
            Sender = _parent.Runtime.Endpoints.GetOrBuildSendingAgent(_endpoint.Uri)
        };

        if (messageType != null)
        {
            envelope.SetMessageType(messageType);
            
            var route = _endpoint.RouteFor(messageType, _parent.Runtime);
            foreach (var rule in route.Rules) rule.Modify(envelope);
        }
        
        configure?.Invoke(envelope);
        
        // adjust for local, scheduled send
        if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow) && !_endpoint.Agent!.SupportsNativeScheduledSend)
        {
            var localDurableQueue =
                _parent.Runtime.Endpoints.GetOrBuildSendingAgent(TransportConstants.DurableLocalUri);
            envelope = envelope.ForScheduledSend(localDurableQueue);
        }

        _parent.TrackEnvelopeCorrelation(envelope, Activity.Current);

        return _parent.PersistOrSendAsync(envelope);
    }

    public Task<Acknowledgement> InvokeAsync(object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = _endpoint.RouteFor(message.GetType(), _parent.Runtime);
        return route.InvokeAsync<Acknowledgement>(message, _parent, cancellation, timeout);
    }

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        where T : class
    {
        _parent.Runtime.RegisterMessageType(typeof(T));

        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = _endpoint.RouteFor(message.GetType(), _parent.Runtime);
        return route.InvokeAsync<T>(message, _parent, cancellation, timeout);
    }

    public async Task CancelScheduledAsync(object schedulingToken, CancellationToken cancellation = default)
    {
        var agent = _endpoint.Agent;
        if (agent == null)
        {
            throw new InvalidOperationException($"No sending agent is configured for endpoint {_endpoint.Uri}");
        }

        var cancelSender = resolveCancelSender(agent);
        if (cancelSender == null)
        {
            throw new NotSupportedException(
                $"The transport at {_endpoint.Uri} does not support cancelling scheduled messages.");
        }

        await cancelSender.CancelScheduledMessageAsync(schedulingToken, cancellation);
    }

    private ISenderWithScheduledCancellation? resolveCancelSender(ISendingAgent agent)
    {
        // The agent itself may implement it (e.g., StubEndpoint)
        if (agent is ISenderWithScheduledCancellation directCancel)
        {
            return directCancel;
        }

        // InlineSendingAgent exposes Sender publicly
        if (agent is InlineSendingAgent inlineAgent)
        {
            if (inlineAgent.Sender is TenantedSender tenantedSender)
            {
                var innerSender = tenantedSender.SenderForTenantId(_parent.TenantId);
                return innerSender as ISenderWithScheduledCancellation;
            }

            return inlineAgent.Sender as ISenderWithScheduledCancellation;
        }

        return null;
    }
}