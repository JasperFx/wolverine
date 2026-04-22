using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Grpc;

public class GrpcTransport : TransportBase<GrpcEndpoint>
{
    private readonly LightweightCache<Uri, GrpcEndpoint> _endpoints;

    public GrpcTransport() : base("grpc", "gRPC Transport", [])
    {
        _endpoints = new LightweightCache<Uri, GrpcEndpoint>(uri => new GrpcEndpoint(uri));
    }

    protected override IEnumerable<GrpcEndpoint> endpoints() => _endpoints;

    protected override GrpcEndpoint findEndpointByUri(Uri uri) => _endpoints[uri];

    public GrpcEndpoint EndpointForLocalPort(int port)
    {
        var uri = new Uri($"grpc://localhost:{port}");
        return _endpoints[uri];
    }

    public GrpcEndpoint EndpointFor(string host, int port)
    {
        var uri = new Uri($"grpc://{host}:{port}");
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
}
