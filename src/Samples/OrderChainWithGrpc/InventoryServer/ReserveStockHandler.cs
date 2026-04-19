using OrderChainWithGrpc.Contracts;
using Wolverine;

namespace OrderChainWithGrpc.InventoryServer;

/// <summary>
///     Wolverine handler for <see cref="ReserveStock"/>. The gRPC service forwards to the bus
///     and the bus invokes this handler — the handler itself has no gRPC coupling.
///     <para>
///         The envelope identifiers read from <see cref="IMessageContext"/> below were *not*
///         stamped by code in this process. They were unpacked from inbound gRPC metadata by
///         <c>WolverineGrpcServicePropagationInterceptor</c>, which is wired up automatically
///         by <c>AddWolverineGrpc()</c>. Echoing them back on the reply is what lets the sample
///         visually prove the chain preserved identity end-to-end.
///     </para>
/// </summary>
public static class ReserveStockHandler
{
    public static ReservationConfirmed Handle(ReserveStock cmd, IMessageContext context)
    {
        if (string.Equals(cmd.Sku, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            // Throwing a plain .NET exception — no gRPC vocabulary in the handler.
            // The server-side WolverineGrpcExceptionInterceptor maps this to StatusCode.NotFound,
            // and the upstream OrderServer's client-side WolverineGrpcClientExceptionInterceptor
            // translates that back to KeyNotFoundException at its call site.
            throw new KeyNotFoundException($"SKU '{cmd.Sku}' is not stocked");
        }

        return new ReservationConfirmed
        {
            ReservationId = Guid.NewGuid().ToString("N"),
            CorrelationIdFromUpstream = context.CorrelationId ?? string.Empty,
            TenantIdFromUpstream = context.TenantId ?? string.Empty,
            ParentIdFromUpstream = context.Envelope?.ParentId ?? string.Empty
        };
    }
}
