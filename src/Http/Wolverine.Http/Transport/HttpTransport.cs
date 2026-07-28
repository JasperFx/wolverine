using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

public class HttpTransport : TransportBase<HttpEndpoint>
{
    private readonly LightweightCache<Uri, HttpEndpoint> _endpoints
        = new(uri => new HttpEndpoint(uri, EndpointRole.Application){OutboundUri = uri.ToString()});

    public HttpTransport() : base("https", "HTTP Transport", ["http"])
    {
    }

    /// <summary>
    /// Name of the transport's own <see cref="IHttpClientFactory"/> client, registered by
    /// <c>AddWolverineHttp()</c>. Transport sends resolve it whenever the destination has no named client
    /// of its own, so envelope traffic carries transport configuration instead of inheriting whatever the
    /// application configured on its default <see cref="HttpClient"/>. Configure it like any other named
    /// client — <c>services.AddHttpClient(HttpTransport.HttpClientName, c =&gt; …)</c> — to set a timeout,
    /// a handler, or a proxy across every HTTP-transport send.
    /// </summary>
    public const string HttpClientName = "Wolverine.Http.Transport";

    public const string EnvelopeContentType = "binary/wolverine-envelope";
    public const string EnvelopeBatchContentType = "binary/wolverine-envelopes";
    public const string CloudEventsContentType = "application/cloudevents+json";
    public const string CloudEventsBatchContentType = "application/cloudevents-batch+json";

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