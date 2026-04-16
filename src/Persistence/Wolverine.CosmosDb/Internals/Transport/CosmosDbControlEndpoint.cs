using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.CosmosDb.Internals.Transport;

internal class CosmosDbControlEndpoint : Endpoint
{
    private readonly CosmosDbControlTransport _parent;

    public CosmosDbControlEndpoint(CosmosDbControlTransport parent, Guid nodeId) : base(
        new Uri($"{CosmosDbControlTransport.ProtocolName}://{nodeId}"), EndpointRole.System)
    {
        NodeId = nodeId;
        _parent = parent;
        Mode = EndpointMode.BufferedInMemory;
        MaxDegreeOfParallelism = 1;

        // No otel for this one!
        TelemetryEnabled = false;
    }

    public Guid NodeId { get; }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return new ValueTask<IListener>(new CosmosDbControlListener(_parent, this, receiver,
            runtime.LoggerFactory.CreateLogger<CosmosDbControlListener>(), runtime.Options.Durability.Cancellation));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new CosmosDbControlSender(this, _parent, runtime.LoggerFactory.CreateLogger<CosmosDbControlSender>(),
            runtime.Options.Durability.Cancellation);
    }
}
