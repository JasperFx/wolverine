using Grpc.Core;
using ProtoContracts;
using Wolverine;
using Wolverine.Http.Grpc;

namespace ProtoFirstServer;

/// <summary>
/// Proto-first gRPC service that bridges the Greeter contract to the Wolverine message bus.
///
/// Key differences from the code-first approach:
/// <list type="bullet">
///   <item>Inherits from the PROTO-GENERATED <c>Greeter.GreeterBase</c> (not <c>WolverineGrpcEndpointBase</c>)</item>
///   <item><c>IMessageBus</c> is injected via the CONSTRUCTOR (standard DI), not via a property</item>
///   <item><c>[WolverineGrpcService]</c> attribute enables automatic discovery by
///         <c>MapWolverineGrpcEndpoints()</c> since the naming convention still requires
///         <c>WolverineGrpcEndpointBase</c> as the base class</item>
/// </list>
/// </summary>
[WolverineGrpcService]
public class GreeterService : Greeter.GreeterBase
{
    private readonly IMessageBus _bus;

    public GreeterService(IMessageBus bus) => _bus = bus;

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => _bus.InvokeAsync<HelloReply>(request, context.CancellationToken);
}
