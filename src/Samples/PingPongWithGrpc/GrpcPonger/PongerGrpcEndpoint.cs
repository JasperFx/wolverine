using GrpcPingPongContracts;
using ProtoBuf.Grpc;
using Wolverine.Http.Grpc;

namespace GrpcPonger;

/// <summary>
/// Wolverine gRPC endpoint that handles IPongerService requests via the message bus.
/// Inherits WolverineGrpcEndpointBase and uses convention-based discovery (name ends with "GrpcEndpoint").
/// </summary>
public class PongerGrpcEndpoint : WolverineGrpcEndpointBase, IPongerService
{
    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => Bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
