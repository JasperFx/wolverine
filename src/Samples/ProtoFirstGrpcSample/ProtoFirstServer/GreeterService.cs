using Grpc.Core;
using ProtoContracts;
using Wolverine;
using Wolverine.Http.Grpc;

namespace ProtoFirstServer;

/// <summary>
/// Proto-first gRPC service demonstrating Wolverine integration with proto-generated base classes.
/// Uses constructor injection of IMessageBus (standard DI) since proto-first services cannot
/// inherit WolverineGrpcEndpointBase. [WolverineGrpcService] enables automatic discovery.
/// </summary>
[WolverineGrpcService]
public class GreeterService : Greeter.GreeterBase
{
    private readonly IMessageBus _bus;

    public GreeterService(IMessageBus bus) => _bus = bus;

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => _bus.InvokeAsync<HelloReply>(request, context.CancellationToken);
}
