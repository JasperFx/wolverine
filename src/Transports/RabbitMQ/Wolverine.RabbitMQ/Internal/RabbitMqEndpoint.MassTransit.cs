using JasperFx.Core;
using Wolverine.Runtime.Interop.MassTransit;

namespace Wolverine.RabbitMQ.Internal;

public abstract partial class RabbitMqEndpoint : IMassTransitInteropEndpoint
{
    public Uri? MassTransitUri()
    {
        var segments = new List<string>();
        var virtualHost = _parent.ConnectionFactory.VirtualHost;
        if (virtualHost.IsNotEmpty() && virtualHost != "/")
        {
            segments.Add(virtualHost);
        }

        var routingKey = RoutingKey();
        if (routingKey.IsNotEmpty())
        {
            segments.Add(routingKey);
        }
        else if (ExchangeName.IsNotEmpty())
        {
            segments.Add(ExchangeName);
        }
        else
        {
            return null;
        }

        return $"{_parent.Protocol}://{_parent.ConnectionFactory.HostName}/{segments.Join("/")}".ToUri();
    }

    public Uri? MassTransitReplyUri()
    {
        if (_parent.ReplyEndpoint() is RabbitMqEndpoint r)
        {
            return r.MassTransitUri();
        }

        return null;
    }

    public Uri? TranslateMassTransitToWolverineUri(Uri uri)
    {
        var lastSegment = uri.Segments.LastOrDefault();
        if (lastSegment.IsNotEmpty())
        {
            return $"rabbitmq://queue/{lastSegment}".ToUri();
        }

        return null;
    }

    public void UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        _customizeMapping = m =>
        {
            m.InteropWithMassTransit(configure);
        };
    }
}