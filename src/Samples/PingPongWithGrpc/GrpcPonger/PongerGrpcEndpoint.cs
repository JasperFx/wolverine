using PingPongContracts;
using ProtoBuf.Grpc;
using Wolverine.Http.Grpc;

namespace GrpcPonger;

/// <summary>
/// Wolverine gRPC endpoint that bridges the IPongerService gRPC contract to the
/// Wolverine message bus. Incoming gRPC calls are dispatched as Wolverine commands
/// and the response is returned to the caller.
///
/// Convention discovery applies: this class inherits WolverineGrpcEndpointBase
/// and its name ends with "GrpcEndpoint", so it is automatically registered
/// when MapWolverineGrpcEndpoints() is called.
/// </summary>
public class PongerGrpcEndpoint : WolverineGrpcEndpointBase, IPongerService
{
    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => Bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
