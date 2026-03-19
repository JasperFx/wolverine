using Grpc.Core;
using ProtoContracts;
using Wolverine;
using Wolverine.Http.Grpc;

namespace ProtoFirstServer;

/// <summary>
/// Proto-first gRPC service demonstrating Wolverine integration with proto-generated base classes.
/// </summary>
/// <remarks>
/// <para><strong>MUST inherit Greeter.GreeterBase</strong>: This proto-generated base class is required
/// by ASP.NET Core's gRPC infrastructure. There is no way to remove it or replace it with
/// WolverineGrpcEndpointBase due to C#'s single inheritance limitation.</para>
/// <para><strong>MUST use [WolverineGrpcService]</strong>: Required for automatic discovery since
/// convention-based discovery only works with WolverineGrpcEndpointBase.</para>
/// <para><strong>IMessageBus field is optional</strong>: Only inject via constructor if you actually
/// call _bus methods. If you handle requests inline without delegating to handlers, you can omit it.</para>
/// </remarks>
[WolverineGrpcService]
public class GreeterService : Greeter.GreeterBase
{
    // IMessageBus is injected because we use it in SayHello below.
    // If you don't call _bus.InvokeAsync, you don't need this field or constructor parameter.
    private readonly IMessageBus _bus;

    public GreeterService(IMessageBus bus) => _bus = bus;

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => _bus.InvokeAsync<HelloReply>(request, context.CancellationToken);
}
