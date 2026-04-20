using ProtoBuf.Grpc;

namespace Wolverine.Grpc.Tests;

/// <summary>
///     Code-first gRPC service that forwards to the faulting Wolverine handlers.
///     The "GrpcService" suffix means <c>MapWolverineGrpcServices()</c> would pick it up
///     automatically if discovery were used.
/// </summary>
public class FaultingGrpcService : WolverineGrpcServiceBase, IFaultingService
{
    public FaultingGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<FaultCodeFirstReply> Throw(FaultCodeFirstRequest request, CallContext context = default)
        => Bus.InvokeAsync<FaultCodeFirstReply>(request, context.CancellationToken);

    public IAsyncEnumerable<FaultCodeFirstReply> ThrowStream(FaultStreamCodeFirstRequest request, CallContext context = default)
        => Bus.StreamAsync<FaultCodeFirstReply>(request, context.CancellationToken);
}
