using System.Collections;
using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
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

                // CritterWatch GH-907: whatever endpoint carries node control traffic is system
                // traffic by definition. The database and shared-memory control endpoints are born
                // with the System role, but a generic endpoint promoted to control duty (e.g.
                // UseTcpForControlEndpoint) was not — leaving its agent-command traffic visible to
                // metrics as apparent application volume.
                value.Role = EndpointRole.System;

                // GH-1670 follow-up: node control traffic is nothing but IAgentCommand executions
                // and their replies. The receive span and the pipeline-level execution span are
                // gated by the ENDPOINT's telemetry flag, not the agent-command chain's, so a
                // broker control queue (EnableWolverineControlQueues) or TCP control endpoint was
                // still publishing send/receive/execution Open Telemetry spans for every agent
                // command. The database control endpoint is already born with telemetry off; this
                // closes the same hole for promoted endpoints.
                value.TelemetryEnabled = false;
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
        return _transports.TryGetValue(scheme.ToLowerInvariant(), out var transport)
            ? transport
            : null;
    }

    public void Add(ITransport transport)
    {
        _transports[transport.Protocol] = transport;
    }

    public T GetOrCreate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(BrokerName? name = null) where T : ITransport, new()
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
            var transport = _transports.Values.OfType<T>().FirstOrDefault(x => x.Protocol == name.Name);
            if (transport == null)
            {
                transport = (T)Activator.CreateInstance(typeof(T), name.Name)!;
                _transports[name.Name] = transport!;
            }

            return transport!;
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