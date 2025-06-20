using System.Collections;
using JasperFx.Core;
using JasperFx.Core.Reflection;
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
    private Endpoint? _nodeControlEndpoint;

    internal TransportCollection()
    {
        Add(new StubTransport());
        Add(new LocalTransport());
        Add(new TcpTransport());
    }

    /// <summary>
    ///     The endpoint to use for sending system messages to a specific Node
    /// </summary>
    public Endpoint? NodeControlEndpoint
    {
        get => _nodeControlEndpoint;
        set
        {
            if (value != null)
            {
                value.IsListener = true;
            }

            _nodeControlEndpoint = value;
        }
    }

    internal IEnumerable<IEndpointPolicy> EndpointPolicies => _policies;

    ValueTask IAsyncDisposable.DisposeAsync() =>
        _transports.Values.MaybeDisposeAllAsync();

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
        return _transports.TryGetValue(scheme, out var transport)
            ? transport
            : null;
    }

    public void Add(ITransport transport)
    {
        _transports[transport.Protocol] = transport;
    }

    public T GetOrCreate<T>(BrokerName? name = null) where T : ITransport, new()
    {
        if (name == null)
        {
            var transport = _transports.Values.OfType<T>().FirstOrDefault();
            if (transport == null)
            {
                transport = new T();
                _transports[transport.Protocol] = transport;
            }

            return transport;
        }
        else
        {
            if (!_transports.TryGetValue(name.Name, out var transport))
            {
                transport = (T)Activator.CreateInstance(typeof(T), name.Name);
                _transports[name.Name] = transport;
            }

            return transport.As<T>();
        }
        
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

    internal void RemoveLocal()
    {
        _transports.Remove(TransportConstants.Local);
    }
}