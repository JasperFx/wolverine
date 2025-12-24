using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

public class HttpTransport : TransportBase<HttpEndpoint>
{
    private readonly LightweightCache<Uri, HttpEndpoint> _endpoints
        = new(uri => new HttpEndpoint(uri, EndpointRole.Application){OutboundUri = uri.ToString()});

    public HttpTransport() : base("https", "HTTP Transport")
    {
    }

    public const string EnvelopeContentType = "binary/wolverine-envelope";
    public const string EnvelopeBatchContentType = "binary/wolverine-envelopes";

    protected override IEnumerable<HttpEndpoint> endpoints()
    {
        return _endpoints;
    }

    protected override HttpEndpoint findEndpointByUri(Uri uri)
    {
        return _endpoints[uri];
    }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in _endpoints)
        {
            endpoint.Compile(runtime);
        }

        return ValueTask.CompletedTask;
    }

    public HttpEndpoint EndpointFor(string url)
    {
        var uri = new Uri(url);
        return _endpoints[uri];
    }
}