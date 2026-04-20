using OrderChainWithGrpc.Contracts;
using ProtoBuf.Grpc;
using Wolverine;
using Wolverine.Grpc;

namespace OrderChainWithGrpc.OrderServer;

/// <summary>
///     Upstream code-first gRPC service. Same shape as every other Wolverine code-first service —
///     a one-line forward to <see cref="IMessageBus.InvokeAsync{T}"/>. The downstream call
///     to <c>InventoryServer</c> happens inside <c>PlaceOrderHandler</c>, not here.
/// </summary>
public class OrderGrpcService : WolverineGrpcServiceBase, IOrderService
{
    public OrderGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<OrderAccepted> PlaceOrder(PlaceOrder cmd, CallContext context = default)
        => Bus.InvokeAsync<OrderAccepted>(cmd, context.CancellationToken);
}
