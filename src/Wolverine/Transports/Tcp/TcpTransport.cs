using System;
using System.Collections.Generic;
using Baseline;

namespace Wolverine.Transports.Tcp;

public class TcpTransport : TransportBase<TcpEndpoint>
{
    private readonly LightweightCache<Uri, TcpEndpoint> _listeners =
        new(uri =>
        {
            var endpoint = new TcpEndpoint();
            endpoint.Parse(uri);

            return endpoint;
        });

    public TcpTransport() :
        base("tcp", "TCP Sockets")
    {
    }

    protected override IEnumerable<TcpEndpoint> endpoints()
    {
        return _listeners;
    }

    protected override TcpEndpoint findEndpointByUri(Uri uri)
    {
        return _listeners[uri];
    }
}
