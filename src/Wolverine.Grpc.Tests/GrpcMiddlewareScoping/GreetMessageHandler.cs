using Wolverine.Grpc.Tests.GrpcMiddlewareScoping.Generated;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     Wolverine handler for the unary RPC. Records its invocation against the shared
///     <see cref="MiddlewareInvocationSink"/> so middleware-ordering tests can assert
///     before/after relative to the handler call.
/// </summary>
public static class GreetMessageHandler
{
    public const string Marker = "Handler";

    public static GreetReply Handle(GreetRequest request, MiddlewareInvocationSink sink)
    {
        sink.Record(Marker);
        return new GreetReply { Message = $"Hello, {request.Name}" };
    }

    public static async IAsyncEnumerable<GreetReply> Handle(GreetManyRequest request, MiddlewareInvocationSink sink)
    {
        sink.Record(Marker);
        for (var i = 0; i < 3; i++)
        {
            yield return new GreetReply { Message = $"Hello #{i}, {request.Name}" };
            await Task.Yield();
        }
    }
}
