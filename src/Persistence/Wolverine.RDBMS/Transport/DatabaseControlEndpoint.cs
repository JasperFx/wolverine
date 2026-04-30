using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RDBMS.Transport;

internal class DatabaseControlEndpoint : Endpoint
{
    private readonly DatabaseControlTransport _parent;

    public DatabaseControlEndpoint(DatabaseControlTransport parent, Guid nodeId) : base(
        new Uri($"{DatabaseControlTransport.ProtocolName}://{nodeId}"), EndpointRole.System)
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

    // The control transport polls the DB control queue table on a 1s tick and
    // dispatches inter-node agent commands. Forcing it to Durable would route
    // every control envelope through the same store-backed inbox/outbox the
    // durability agent itself owns (deadlock); forcing Inline would skip the
    // batched-poll semantics. Lock to BufferedInMemory regardless of any
    // global endpoint policy. Built-in policies already guard via SupportsMode;
    // direct Mode-setter abuse will throw via the base setter.
    protected override bool supportsMode(EndpointMode mode) => mode == EndpointMode.BufferedInMemory;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return new ValueTask<IListener>(new DatabaseControlListener(_parent, this, receiver,
            runtime.LoggerFactory.CreateLogger<DatabaseControlListener>(), runtime.Options.Durability.Cancellation));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new DatabaseControlSender(this, _parent, runtime.LoggerFactory.CreateLogger<DatabaseControlSender>(),
            runtime.Options.Durability.Cancellation);
    }
}