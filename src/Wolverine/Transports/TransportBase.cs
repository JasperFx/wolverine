using JasperFx.Core;
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

    /// <summary>
    ///     See <see cref="ITransport.AdditionalProtocols" />. Declared here as a virtual rather than left to the
    ///     interface's default implementation for the same reason as
    ///     <see cref="TryResolveListenerAddress" />: a derived transport inherits this class's interface map,
    ///     so an override it declares against the interface default would never be called.
    /// </summary>
    public virtual IEnumerable<string> AdditionalProtocols => [];

    /// <summary>
    ///     See <see cref="ITransport.EndpointNamesAreCaseSensitive" />. Declared here as a virtual for the same reason
    ///     as <see cref="AdditionalProtocols" />.
    /// </summary>
    public virtual bool EndpointNamesAreCaseSensitive => true;

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
        if (uri.Scheme != Protocol && !AdditionalProtocols.Contains(uri.Scheme))
        {
            var accepted = new[] { Protocol }.Concat(AdditionalProtocols).Select(x => $"'{x}'").Join(" or ");
            throw new ArgumentOutOfRangeException($"Uri must have scheme {accepted}, but received {uri.Scheme}");
        }

        return findEndpointWithOriginalName(uri) ?? findEndpointByUri(uri);
    }

    public Endpoint TryGetEndpoint(Uri uri)
    {
        return findEndpointWithOriginalName(uri) ?? findEndpointByUri(uri);
    }

    /// <summary>
    ///     GH-5821. An existing endpoint whose Uri was written exactly like this one, upper case letters and all.
    ///     <see cref="Uri.Host" /> is lower cased by <see cref="Uri" />, so a transport that looks an endpoint up by its
    ///     host turned a configured <c>Orders</c> into a second, lower case <c>orders</c> endpoint the first time the
    ///     configured endpoint's own Uri was handed back to it -- <c>PublishMessage&lt;T&gt;().To(queue.Uri)</c> does
    ///     exactly that -- and the publishing rule landed on the copy. Only a host with upper case letters can lose
    ///     anything, so every other Uri goes straight to the transport's own lookup.
    /// </summary>
    private TEndpoint? findEndpointWithOriginalName(Uri uri)
    {
        var host = EndpointNameCollisions.OriginalHost(uri);
        if (!host.Any(char.IsUpper))
        {
            return null;
        }

        return endpoints().FirstOrDefault(x => x.Uri.OriginalString == uri.OriginalString);
    }

    /// <summary>
    ///     See <see cref="ITransport.TryResolveListenerAddress"/>. Declared here as a virtual rather than left to
    ///     the interface's default implementation because a derived transport that does not re-list
    ///     <see cref="ITransport"/> in its own base list inherits this class's interface map, and a matching method
    ///     it declares would silently never be called.
    /// </summary>
    public virtual Uri? TryResolveListenerAddress(Uri receivedAt) => null;

    /// <summary>
    ///     See <see cref="ITransport.BuildHealthCheck"/>. Declared here as a virtual for the same reason as
    ///     <see cref="TryResolveListenerAddress"/>: a transport's own BuildHealthCheck was reachable only through
    ///     its concrete type, so Wolverine.HealthChecks and anything else enumerating
    ///     <c>WolverineOptions.Transports</c> got null for every broker. See GH-5963.
    /// </summary>
    public virtual WolverineTransportHealthCheck? BuildHealthCheck(IWolverineRuntime runtime) => null;

    public virtual bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = default;
        return false;
    }

    protected abstract IEnumerable<TEndpoint> endpoints();

    protected abstract TEndpoint findEndpointByUri(Uri uri);
}