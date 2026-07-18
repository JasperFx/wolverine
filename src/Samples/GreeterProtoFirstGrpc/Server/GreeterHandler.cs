using System.Runtime.CompilerServices;
using GreeterProtoFirstGrpc.Messages;

namespace GreeterProtoFirstGrpc.Server;

/// <summary>
///     Wolverine handlers for the proto-generated Greeter service. These are ordinary
///     Wolverine handlers — the gRPC protocol layer is supplied entirely by the generated
///     <c>GreeterGrpcHandler</c> wrapper that Wolverine emits at startup.
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

    // Client streaming: the generated wrapper adapts the RPC's inbound stream to
    // IAsyncEnumerable<HelloRequest> and forwards it via IMessageBus.InvokeStreamAsync,
    // so the handler folds N requests into one reply.
    public static async Task<GreetingSummary> Handle(
        IAsyncEnumerable<HelloRequest> requests,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await foreach (var request in requests.WithCancellation(cancellationToken))
        {
            names.Add(request.Name);
        }

        return new GreetingSummary
        {
            Count = names.Count,
            Message = names.Count == 0 ? "Hello, nobody" : $"Hello, {string.Join(" & ", names)}"
        };
    }

    public static FaultReply Handle(FaultRequest request)
        => throw FaultExceptions.Throw(request.Kind);
}
