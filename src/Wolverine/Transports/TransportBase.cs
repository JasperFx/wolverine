using JasperFx.Resources;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports;

public abstract class TransportBase<TEndpoint> : ITransport where TEndpoint : Endpoint
{
    public TransportBase(string protocol, string name)
    {
        Protocol = protocol;
        Name = name;
    }

    public string Name { get; }

    public string Protocol { get; }

    public IEnumerable<Endpoint> Endpoints()
    {
        return endpoints();
    }

    public virtual ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in Endpoints())
        {
            endpoint.Compile(runtime);
        }

        // Nothing
        return ValueTask.CompletedTask;
    }

    public virtual Endpoint? ReplyEndpoint()
    {
        var listeners = endpoints().Where(x => x.IsListener).ToArray();

        return listeners.Length switch
        {
            0 => null,
            1 => listeners.Single(),
            _ => listeners.FirstOrDefault(x => x.IsUsedForReplies) ?? listeners.First()
        };
    }

    public Endpoint GetOrCreateEndpoint(Uri uri)
    {
        if (uri.Scheme != Protocol)
        {
            throw new ArgumentOutOfRangeException($"Uri must have scheme '{Protocol}', but received {uri.Scheme}");
        }

        return findEndpointByUri(uri);
    }

    public Endpoint TryGetEndpoint(Uri uri)
    {
        return findEndpointByUri(uri);
    }

    public virtual bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = default;
        return false;
    }

    protected abstract IEnumerable<TEndpoint> endpoints();

    protected abstract TEndpoint findEndpointByUri(Uri uri);
}