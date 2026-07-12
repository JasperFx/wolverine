using ProtoBuf.Grpc;
using Wolverine.Grpc;

namespace Wolverine.Grpc.Tests.SagaOverGrpc;

/// <summary>
///     Code-first gRPC service that forwards <see cref="StartCountingRequest"/> to the Wolverine bus
///     exactly the way the docs show — <c>Bus.InvokeAsync&lt;TResponse&gt;(request, ct)</c>. The saga
///     on the other side is an ordinary Wolverine handler; nothing here is saga-aware.
/// </summary>
public class CountingSagaGrpcService : WolverineGrpcServiceBase, ICountingSagaService
{
    public CountingSagaGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<StartCountingReply> Start(StartCountingRequest request, CallContext context = default)
        => Bus.InvokeAsync<StartCountingReply>(request, context.CancellationToken);
}
