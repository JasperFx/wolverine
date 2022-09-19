using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports;

public abstract class TransportBase<TEndpoint> : ITransport where TEndpoint : Endpoint
{
    public TransportBase(string protocol, string name)
    {
        Protocols.Add(protocol);
        Name = name;
    }

    public TransportBase(IEnumerable<string> protocols, string name)
    {
        foreach (var protocol in protocols) Protocols.Add(protocol);

        Name = name;
    }

    public string Name { get; }

    public ICollection<string> Protocols { get; } = new List<string>();

    public IEnumerable<Endpoint> Endpoints()
    {
        return endpoints();
    }

    public virtual ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        // Nothing
        return ValueTask.CompletedTask;
    }

    public Endpoint? ReplyEndpoint()
    {
        var listeners = endpoints().Where(x => x.IsListener).ToArray();

        return listeners.Length switch
        {
            0 => null,
            1 => listeners.Single(),
            _ => listeners.FirstOrDefault(x => x.IsUsedForReplies) ?? listeners.First()
        };
    }

    public void StartSenders(IWolverineRuntime root)
    {
        var replyUri = ReplyEndpoint()?.Uri;

        foreach (var endpoint in endpoints().Where(x => x.Subscriptions.Any())) endpoint.StartSending(root, replyUri);
    }

    public Endpoint ListenTo(Uri uri)
    {
        var endpoint = findEndpointByUri(uri);
        endpoint.IsListener = true;

        return endpoint;
    }

    public Endpoint GetOrCreateEndpoint(Uri uri)
    {
        return findEndpointByUri(uri);
    }

    public Endpoint TryGetEndpoint(Uri uri)
    {
        return findEndpointByUri(uri);
    }

    protected abstract IEnumerable<TEndpoint> endpoints();

    protected abstract TEndpoint findEndpointByUri(Uri uri);
}
