using System.ServiceModel;
using ProtoBuf;
using ProtoBuf.Grpc;

namespace OrderChainWithGrpc.Contracts;

/// <summary>
///     Upstream contract, called by an arbitrary external client (e.g. <c>OrderClient</c>).
///     The <c>OrderServer</c> implementation is a Wolverine gRPC service — its handler
///     then calls <see cref="IInventoryService"/> on the downstream <c>InventoryServer</c>
///     via the typed client registered with
///     <c>AddWolverineGrpcClient&lt;IInventoryService&gt;()</c>.
/// </summary>
[ServiceContract]
public interface IOrderService
{
    Task<OrderAccepted> PlaceOrder(PlaceOrder cmd, CallContext context = default);
}

/// <summary>
///     Downstream contract, called from inside the <c>OrderServer</c>'s Wolverine handler.
///     <see cref="IMessageContext"/> seen inside the handler for this service should carry the
///     envelope identifiers stamped by the upstream — no user code needs to forward them.
/// </summary>
[ServiceContract]
public interface IInventoryService
{
    Task<ReservationConfirmed> Reserve(ReserveStock cmd, CallContext context = default);
}

[ProtoContract]
public class PlaceOrder
{
    [ProtoMember(1)]
    public string Sku { get; set; } = string.Empty;

    [ProtoMember(2)]
    public int Quantity { get; set; }
}

[ProtoContract]
public class ReserveStock
{
    [ProtoMember(1)]
    public string Sku { get; set; } = string.Empty;

    [ProtoMember(2)]
    public int Quantity { get; set; }
}

/// <summary>
///     Upstream reply. <see cref="CorrelationIdSeenAtBothHops"/> is populated from the
///     downstream reply so an external caller can eyeball that propagation survived both hops.
/// </summary>
[ProtoContract]
public class OrderAccepted
{
    [ProtoMember(1)]
    public string ReservationId { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string CorrelationIdSeenAtBothHops { get; set; } = string.Empty;
}

/// <summary>
///     Downstream reply. Echoes the envelope identifiers the downstream handler read from
///     its own <see cref="IMessageContext"/> — those values were put there by the server-side
///     propagation interceptor, not by user code. Round-tripping them here is what makes
///     the sample's claim visually verifiable.
/// </summary>
[ProtoContract]
public class ReservationConfirmed
{
    [ProtoMember(1)]
    public string ReservationId { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string CorrelationIdFromUpstream { get; set; } = string.Empty;

    [ProtoMember(3)]
    public string TenantIdFromUpstream { get; set; } = string.Empty;

    [ProtoMember(4)]
    public string ParentIdFromUpstream { get; set; } = string.Empty;
}
