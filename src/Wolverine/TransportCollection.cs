using System.Collections;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Stub;
using Wolverine.Transports.Tcp;

namespace Wolverine;

public class TransportCollection : IEnumerable<ITransport>, IAsyncDisposable
{
    private readonly List<IEndpointPolicy> _policies = new();
    private readonly Dictionary<string, ITransport> _transports = new();

    internal TransportCollection()
    {
        Add(new StubTransport());
        Add(new LocalTransport());
        Add(new TcpTransport());
    }

    internal IEnumerable<IEndpointPolicy> EndpointPolicies => _policies;

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        // TODO -- this is generic. Harvest this to somewhere else
        foreach (var transport in _transports.Values)
        {
            if (transport is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else if (transport is IDisposable d)
            {
                d.Dispose();
            }
        }
    }

    public IEnumerator<ITransport> GetEnumerator()
    {
        return _transports.Values.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    internal void AddPolicy(IEndpointPolicy policy)
    {
        _policies.Add(policy);
    }

    public ITransport? ForScheme(string scheme)
    {
        return _transports.TryGetValue(scheme.ToLowerInvariant(), out var transport)
            ? transport
            : null;
    }

    public void Add(ITransport transport)
    {
        _transports[transport.Protocol] = transport;
    }

    public T GetOrCreate<T>() where T : ITransport, new()
    {
        var transport = _transports.Values.OfType<T>().FirstOrDefault();
        if (transport == null)
        {
            transport = new T();
            _transports[transport.Protocol] = transport;
        }

        return transport;
    }

    internal ITransport Find(Uri uri)
    {
        var transport = ForScheme(uri.Scheme);
        if (transport == null)
        {
            throw new InvalidOperationException($"Unknown Transport scheme '{uri.Scheme}'");
        }

        return transport;
    }

    public Endpoint? TryGetEndpoint(Uri uri)
    {
        return Find(uri).TryGetEndpoint(uri);
    }

    public Endpoint GetOrCreateEndpoint(Uri uri)
    {
        return Find(uri).GetOrCreateEndpoint(uri);
    }

    public Endpoint[] AllEndpoints()
    {
        return _transports.Values.SelectMany(x => x.Endpoints()).ToArray();
    }
}