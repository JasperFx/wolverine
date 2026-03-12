using Grpc.Core;
using ProtoContracts;
using Wolverine;
using Wolverine.Http.Grpc;

namespace ProtoFirstServer;

/// <summary>
/// Proto-first gRPC service demonstrating Wolverine integration with proto-generated base classes.
/// With code generation, no constructor boilerplate is needed - Wolverine generates a handler that
/// provides the Bus property automatically. [WolverineGrpcService] enables automatic discovery and code generation.
/// </summary>
[WolverineGrpcService]
public abstract class GreeterService : Greeter.GreeterBase
{
    // Bus property provided by generated handler - no constructor needed!
    protected abstract IMessageBus Bus { get; }

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => Bus.InvokeAsync<HelloReply>(request, context.CancellationToken);
}
