using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RavenDb.Internals.Transport;

internal class RavenDbControlEndpoint : Endpoint
{
    private readonly RavenDbControlTransport _parent;

    public RavenDbControlEndpoint(RavenDbControlTransport parent, Guid nodeId) : base(
        new Uri($"{RavenDbControlTransport.ProtocolName}://{nodeId}"), EndpointRole.System)
    {
        NodeId = nodeId;
        _parent = parent;
        Mode = EndpointMode.BufferedInMemory;
        MaxDegreeOfParallelism = 1;
        BrokerRole = "queue";

        // No otel for this one!
        TelemetryEnabled = false;
    }

    public Guid NodeId { get; }

    // The control transport polls the RavenDB ControlMessages collection on a 1s
    // tick and dispatches inter-node agent commands. Forcing it to Durable would
    // route every control envelope through the same store-backed inbox/outbox the
    // durability agent itself owns (deadlock); forcing Inline would skip the
    // batched-poll semantics. Lock to BufferedInMemory regardless of any global
    // endpoint policy.
    protected override bool supportsMode(EndpointMode mode) => mode == EndpointMode.BufferedInMemory;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return new ValueTask<IListener>(new RavenDbControlListener(_parent, this, receiver,
            runtime.LoggerFactory.CreateLogger<RavenDbControlListener>(), runtime.Options.Durability.Cancellation));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new RavenDbControlSender(this, _parent, runtime.LoggerFactory.CreateLogger<RavenDbControlSender>(),
            runtime.Options.Durability.Cancellation);
    }
}
