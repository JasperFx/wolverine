using PingPongWithGrpc.Messages;
using ProtoBuf.Grpc;
using Wolverine;
using Wolverine.Http.Grpc;

namespace PingPongWithGrpc.Ponger;

/// <summary>
///     Code-first gRPC service that delegates incoming Ping calls to the Wolverine bus.
///     Inherits <see cref="WolverineGrpcServiceBase"/> to get an <see cref="IMessageBus"/>
///     without boilerplate. The "GrpcService" suffix lets
///     <c>MapWolverineGrpcServices()</c> discover this type automatically.
/// </summary>
public class PingGrpcService : WolverineGrpcServiceBase, IPingService
{
    public PingGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<PongReply> Ping(PingRequest request, CallContext context = default)
        => Bus.InvokeAsync<PongReply>(request, context.CancellationToken);
}
