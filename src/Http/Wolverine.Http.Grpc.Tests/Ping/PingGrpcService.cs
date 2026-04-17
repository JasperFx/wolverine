using ProtoBuf.Grpc;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Code-first gRPC service that delegates Ping calls to the Wolverine bus.
/// Inherits WolverineGrpcServiceBase to get IMessageBus without boilerplate.
/// The "GrpcService" suffix means MapWolverineGrpcServices() will discover
/// this type automatically.
/// </summary>
public class PingGrpcService : WolverineGrpcServiceBase, IPingService
{
    public PingGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<PongReply> Ping(PingRequest request, CallContext context = default)
        => Bus.InvokeAsync<PongReply>(request, context.CancellationToken);
}
