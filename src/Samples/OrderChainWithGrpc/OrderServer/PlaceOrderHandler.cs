using OrderChainWithGrpc.Contracts;
using Wolverine;

namespace OrderChainWithGrpc.OrderServer;

/// <summary>
///     Wolverine handler for <see cref="PlaceOrder"/>. This is the sample's entire proof-point.
///     <para>
///         <see cref="IInventoryService"/> is injected because it was registered via
///         <c>AddWolverineGrpcClient&lt;IInventoryService&gt;()</c> in <c>Program.cs</c>. The handler
///         simply calls the downstream service like any other collaborator — no <c>Metadata</c>
///         hand-assembly, no <c>CallOptions</c> wiring, no manual interceptors. The Wolverine
///         client-side propagation interceptor reads the ambient <see cref="IMessageContext"/>
///         and stamps the envelope identifiers on the outgoing call automatically.
///     </para>
/// </summary>
public static class PlaceOrderHandler
{
    public static async Task<OrderAccepted> Handle(
        PlaceOrder cmd,
        IInventoryService inventory,
        CancellationToken cancellationToken)
    {
        var confirmed = await inventory.Reserve(
            new ReserveStock { Sku = cmd.Sku, Quantity = cmd.Quantity });

        return new OrderAccepted
        {
            ReservationId = confirmed.ReservationId,
            // Downstream echoed the correlation-id it saw. If the chain is healthy, this is
            // the same value the upstream IMessageContext was carrying when the handler ran.
            CorrelationIdSeenAtBothHops = confirmed.CorrelationIdFromUpstream
        };
    }
}
