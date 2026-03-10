using GrpcPingPongContracts;
using ProtoBuf.Grpc;
using Wolverine;
using Wolverine.Http.Grpc;

namespace GrpcPonger;

/// <summary>
/// Wolverine gRPC endpoint that bridges the IPongerService gRPC contract to the
/// Wolverine message bus. Incoming gRPC calls are dispatched as Wolverine commands
/// and the response is returned to the caller.
///
/// Uses constructor injection of IMessageBus. The [WolverineGrpcService] attribute
/// enables automatic discovery when MapWolverineGrpcEndpoints() is called.
/// </summary>
[WolverineGrpcService]
public class PongerGrpcEndpoint : IPongerService
{
    private readonly IMessageBus _bus;

    public PongerGrpcEndpoint(IMessageBus bus) => _bus = bus;

    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => _bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
