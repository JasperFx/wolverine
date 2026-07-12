namespace Wolverine.Grpc.Tests.SagaOverGrpc;

/// <summary>
///     A message-identified saga: it is started by <see cref="StartReservationRequest"/> (id from the
///     message) and continued by <see cref="BookReservationRequest"/> (id from the message). No
///     envelope <c>saga-id</c> header is involved, so this is the case that "works just like HTTP"
///     over a gRPC hop. Both handlers return a reply so the forwarding
///     <c>Bus.InvokeAsync&lt;TResponse&gt;</c> has a response to hand back to the RPC caller.
/// </summary>
public class ReservationSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public bool Booked { get; set; }

    public ReservationBookedReply Start(StartReservationRequest start)
    {
        Id = start.ReservationId!;
        return new ReservationBookedReply { ReservationId = start.ReservationId };
    }

    public BookReservationReply Handle(BookReservationRequest book)
    {
        Booked = true;

        // Done — Wolverine deletes the saga state at the end of message handling.
        MarkCompleted();

        return new BookReservationReply { Completed = true };
    }
}
