using System.Runtime.CompilerServices;

namespace Wolverine.Http.Grpc.Tests.ProtoFirst;

/// <summary>
///     Wolverine handlers for the proto-generated Greeter service. These are ordinary Wolverine
///     handlers — the gRPC protocol layer is entirely supplied by the generated GreeterGrpcHandler
///     wrapper that <see cref="GrpcServiceChain"/> emits.
/// </summary>
public static class GreeterHandler
{
    public static HelloReply Handle(HelloRequest request)
        => new() { Message = $"Hello, {request.Name}" };

    public static GoodbyeReply Handle(GoodbyeRequest request)
        => new() { Message = $"Goodbye, {request.Name}" };

    public static async IAsyncEnumerable<HelloReply> Handle(
        StreamGreetingsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new HelloReply { Message = $"Hello, {request.Name} [{i}]" };
            await Task.Yield();
        }
    }

    public static FaultReply Handle(FaultRequest request)
        => throw FaultExceptions.Throw(request.Kind);
}
