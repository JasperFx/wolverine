using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

namespace Wolverine.Grpc.Tests.SagaOverGrpc;

/// <summary>
///     Code-first gRPC contract for a <em>message-identified</em> saga — the id rides on the request
///     DTO (<see cref="StartReservationRequest.ReservationId"/> / <see cref="BookReservationRequest.Id"/>),
///     exactly like the HTTP saga sample (<c>WolverineWebApi.SagaExample</c>). This is the case Jeremy
///     expects to "work just like HTTP", and it does — the gRPC shim forwards to the same
///     <c>InvokeAsync</c> pipeline. Mirrors
///     <c>Wolverine.Http.Tests.building_a_saga_and_publishing_other_messages_from_http_endpoint</c>.
/// </summary>
#region sample_grpc_saga_contracts

[ServiceContract]
public interface IReservationSagaService
{
    Task<ReservationBookedReply> Start(StartReservationRequest request, CallContext context = default);
    Task<BookReservationReply> Book(BookReservationRequest request, CallContext context = default);
}

[ProtoContract]
public class StartReservationRequest
{
    // "ReservationId" matches the ReservationSaga type name minus the "Saga"
    // suffix, so Wolverine resolves the saga identity from this member
    [ProtoMember(1)]
    public string? ReservationId { get; set; }
}

[ProtoContract]
public class ReservationBookedReply
{
    [ProtoMember(1)]
    public string? ReservationId { get; set; }
}

[ProtoContract]
public class BookReservationRequest
{
    // "Id" is also matched as the saga identity by convention
    [ProtoMember(1)]
    public string? Id { get; set; }
}

[ProtoContract]
public class BookReservationReply
{
    [ProtoMember(1)]
    public bool Completed { get; set; }
}

#endregion
