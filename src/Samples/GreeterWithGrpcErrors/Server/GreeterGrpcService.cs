using GreeterWithGrpcErrors.Messages;
using ProtoBuf.Grpc;
using Wolverine;
using Wolverine.Grpc;

namespace GreeterWithGrpcErrors.Server;

/// <summary>
///     Code-first gRPC service that forwards every RPC to the Wolverine bus. The
///     <c>"GrpcService"</c> suffix lets <c>MapWolverineGrpcServices()</c> discover it
///     by convention.
/// </summary>
public class GreeterGrpcService : WolverineGrpcServiceBase, IGreeterService
{
    public GreeterGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<GreetReply> Greet(GreetRequest request, CallContext context = default)
        => Bus.InvokeAsync<GreetReply>(request, context.CancellationToken);

    public Task<FarewellReply> Farewell(FarewellRequest request, CallContext context = default)
        => Bus.InvokeAsync<FarewellReply>(request, context.CancellationToken);
}
