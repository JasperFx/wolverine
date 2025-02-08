using JasperFx.Core;
using JasperFx.Resources;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

internal class HttpTransport : ITransport
{
    private readonly Cache<Uri, HttpEndpoint> _endpoints 
        = new(uri => new HttpEndpoint(uri, EndpointRole.Application));
    
    public string Protocol { get; } = "http";
    public string Name { get; } = "Http Transport";
    public Endpoint? ReplyEndpoint()
    {
        throw new NotImplementedException();
    }

    public Endpoint GetOrCreateEndpoint(Uri uri)
    {
        throw new NotImplementedException();
    }

    public Endpoint? TryGetEndpoint(Uri uri)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<Endpoint> Endpoints()
    {
        throw new NotImplementedException();
    }

    public async ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        throw new NotImplementedException();
    }

    public HttpEndpoint EndpointFor(string url)
    {
        throw new NotImplementedException();
    }
}