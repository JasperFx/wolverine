using ProtoBuf.Grpc;
using Wolverine.Grpc;

namespace Wolverine.Grpc.Tests.SagaOverGrpc;

/// <summary>
///     Code-first gRPC service that forwards both saga messages to the Wolverine bus with the
///     canonical <c>Bus.InvokeAsync&lt;TResponse&gt;</c> shim — nothing here is saga-aware. Proves a
///     message-identified saga can be started and continued over gRPC exactly like the HTTP endpoint
///     equivalent.
/// </summary>
public class ReservationSagaGrpcService : WolverineGrpcServiceBase, IReservationSagaService
{
    public ReservationSagaGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<ReservationBookedReply> Start(StartReservationRequest request, CallContext context = default)
        => Bus.InvokeAsync<ReservationBookedReply>(request, context.CancellationToken);

    public Task<BookReservationReply> Book(BookReservationRequest request, CallContext context = default)
        => Bus.InvokeAsync<BookReservationReply>(request, context.CancellationToken);
}
