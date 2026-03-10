using GrpcPingPongContracts;
using ProtoBuf.Grpc;
using Wolverine.Http.Grpc;

namespace GrpcPonger;

/// <summary>
/// Wolverine gRPC endpoint that bridges the IPongerService gRPC contract to the
/// Wolverine message bus. Incoming gRPC calls are dispatched as Wolverine commands
/// and the response is returned to the caller.
///
/// Inherits WolverineGrpcEndpointBase so that IMessageBus is available via the
/// Bus property (property injection) — no constructor boilerplate required.
/// The [WolverineGrpcService] attribute enables automatic discovery when
/// MapWolverineGrpcEndpoints() is called.
/// </summary>
[WolverineGrpcService]
public class PongerGrpcEndpoint : WolverineGrpcEndpointBase, IPongerService
{
    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => Bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
