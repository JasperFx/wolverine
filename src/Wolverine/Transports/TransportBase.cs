using JasperFx.Descriptors;
using JasperFx.Resources;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;

namespace Wolverine.Transports;

public abstract class TransportBase<TEndpoint> : ITransport, ITagged where TEndpoint : Endpoint
{
    public TransportBase(string protocol, string name, string[] tags)
    {
        Protocol = protocol;
        Name = name;
        Tags = tags ?? throw new ArgumentNullException(nameof(tags));
    }

    public string[] Tags { get; }

    public virtual bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = new BrokerDescription(this);
        return true;
    }

    /// <summary>
    /// A sanitized, credential-free summary of this broker's connection target. The default returns null;
    /// concrete broker transports override to report host/port/namespace/region built from parsed connection
    /// components only — never a raw connection string or any secret. See <see cref="ITransport.DescribeEndpoint"/>.
    /// </summary>
    public virtual string? DescribeEndpoint() => null;

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

    /// <summary>
    ///     See <see cref="ITransport.TryResolveListenerAddress"/>. Declared here as a virtual rather than left to
    ///     the interface's default implementation because a derived transport that does not re-list
    ///     <see cref="ITransport"/> in its own base list inherits this class's interface map, and a matching method
    ///     it declares would silently never be called.
    /// </summary>
    public virtual Uri? TryResolveListenerAddress(Uri receivedAt) => null;

    public virtual bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = default;
        return false;
    }

    protected abstract IEnumerable<TEndpoint> endpoints();

    protected abstract TEndpoint findEndpointByUri(Uri uri);
}