using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Oracle.Transport;

/// <summary>
/// Per-node endpoint for the Oracle-backed control queue. Mirrors
/// <see cref="Wolverine.RDBMS.Transport.DatabaseControlEndpoint"/> but produces Oracle-aware
/// senders and listeners. See #2622.
/// </summary>
internal class OracleControlEndpoint : Endpoint
{
    private readonly OracleControlTransport _parent;

    public OracleControlEndpoint(OracleControlTransport parent, Guid nodeId) : base(
        new Uri($"{OracleControlTransport.ProtocolName}://{nodeId}"), EndpointRole.System)
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

    // The Oracle control transport polls the WOLVERINE_CONTROL_QUEUE table on a
    // 1s tick. Forcing this endpoint to Durable would route every inter-node
    // agent command through the same Oracle store the durability agent owns
    // (deadlock); forcing Inline would defeat the batched-poll semantics. Lock
    // to BufferedInMemory regardless of any global endpoint policy.
    protected override bool supportsMode(EndpointMode mode) => mode == EndpointMode.BufferedInMemory;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return new ValueTask<IListener>(new OracleControlListener(_parent, this, receiver,
            runtime.LoggerFactory.CreateLogger<OracleControlListener>(),
            runtime.Options.Durability.Cancellation));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new OracleControlSender(this, _parent,
            runtime.LoggerFactory.CreateLogger<OracleControlSender>(),
            runtime.Options.Durability.Cancellation);
    }
}
