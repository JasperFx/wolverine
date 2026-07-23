using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.ComplianceTests.ExclusiveListeners;

/// <summary>
/// A deliberately broker-free durable transport used by <see cref="ExclusiveListenerRecoveryCompliance"/>.
///
/// Exclusive (and leader-pinned) listeners can not be local queues, so testing GH-3590 across *every* message
/// store would otherwise require dragging a real message broker into the compliance suite. This transport gives
/// us a durable, non-local listening endpoint whose messages only ever arrive through the transactional inbox —
/// which is exactly the recovery path under test — with no ports to bind and nothing to install.
/// </summary>
public class SingleNodeListenerTransport : TransportBase<SingleNodeListenerEndpoint>
{
    public const string ProtocolName = "single-node-listener";

    public SingleNodeListenerTransport() : base(ProtocolName, "Single Node Listener", [])
    {
        Endpoints = new LightweightCache<string, SingleNodeListenerEndpoint>(name =>
            new SingleNodeListenerEndpoint(name));
    }

    public new LightweightCache<string, SingleNodeListenerEndpoint> Endpoints { get; }

    public static Uri ToUri(string endpointName)
    {
        return new Uri($"{ProtocolName}://{endpointName}");
    }

    // These endpoints are only ever fed from the transactional inbox, so they must never be picked as the
    // reply endpoint for the application.
    public override Endpoint? ReplyEndpoint() => null;

    protected override IEnumerable<SingleNodeListenerEndpoint> endpoints()
    {
        return Endpoints;
    }

    protected override SingleNodeListenerEndpoint findEndpointByUri(Uri uri)
    {
        return Endpoints[uri.Host];
    }
}

public class SingleNodeListenerEndpoint : Endpoint
{
    public SingleNodeListenerEndpoint(string endpointName)
        : base(SingleNodeListenerTransport.ToUri(endpointName), EndpointRole.Application)
    {
        EndpointName = endpointName;
        Mode = EndpointMode.Durable;
        IsListener = true;
        BrokerRole = "single-node-listener";
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode == EndpointMode.Durable;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return ValueTask.FromResult<IListener>(new SingleNodeListener(Uri));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new SingleNodeListenerSender(Uri);
    }
}

/// <summary>
/// There is no wire. Envelopes reach the endpoint's receiver purely through inbox recovery, so the listener
/// only has to exist and be disposable.
/// </summary>
internal class SingleNodeListener : IListener
{
    public SingleNodeListener(Uri address)
    {
        Address = address;
    }

    public Uri Address { get; }

    public IHandlerPipeline? Pipeline => null;

    public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;

    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

    public ValueTask StopAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Sending is only here so the endpoint can be routed to; the compliance tests seed the inbox directly.
/// </summary>
internal class SingleNodeListenerSender : ISender
{
    public SingleNodeListenerSender(Uri destination)
    {
        Destination = destination;
    }

    public bool SupportsNativeScheduledSend => false;

    public Uri Destination { get; }

    public Task<bool> PingAsync() => Task.FromResult(true);

    public ValueTask SendAsync(Envelope envelope) => ValueTask.CompletedTask;
}

public static class SingleNodeListenerTransportExtensions
{
    /// <summary>
    /// Add a durable, broker-free listening endpoint whose listener is only ever active on a single node —
    /// either <see cref="ListenerScope.Exclusive"/> or <see cref="ListenerScope.PinnedToLeader"/>.
    /// </summary>
    public static SingleNodeListenerEndpoint ListenToSingleNodeEndpoint(this WolverineOptions options,
        string endpointName, ListenerScope scope)
    {
        var transport = options.Transports.GetOrCreate<SingleNodeListenerTransport>();
        var endpoint = transport.Endpoints[endpointName];
        endpoint.IsListener = true;
        endpoint.Mode = EndpointMode.Durable;
        endpoint.ListenerScope = scope;

        return endpoint;
    }
}
