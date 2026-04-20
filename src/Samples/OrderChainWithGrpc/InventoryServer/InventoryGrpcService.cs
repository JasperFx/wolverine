using OrderChainWithGrpc.Contracts;
using ProtoBuf.Grpc;
using Wolverine;
using Wolverine.Grpc;

namespace OrderChainWithGrpc.InventoryServer;

/// <summary>
///     Downstream code-first gRPC service. The "GrpcService" suffix lets
///     <c>MapWolverineGrpcServices()</c> discover and map this type automatically — there is no
///     need to call <c>MapGrpcService&lt;T&gt;()</c> explicitly.
/// </summary>
public class InventoryGrpcService : WolverineGrpcServiceBase, IInventoryService
{
    public InventoryGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<ReservationConfirmed> Reserve(ReserveStock cmd, CallContext context = default)
        => Bus.InvokeAsync<ReservationConfirmed>(cmd, context.CancellationToken);
}
